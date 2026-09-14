using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

// PPE(안전모/조끼/마스크) 판정은 safetyvision 전용 모델이 맡고, "사람이 있는가"는 별도의 검증된 범용
// person 탐지 모델(예: COCO 사전학습 yolov8n)이 맡는 2단계 구조. safetyvision 모델의 Person 클래스는
// 실측 결과 신뢰도가 사실상 0에 가까워(원본 프레임 직접 검증 완료) 더 이상 신뢰하지 않는다.
public sealed class OnnxPpeDetector : IPpeDetector, IDisposable
{
    private readonly SafetyVisionOptions _options;
    private readonly ILogger<OnnxPpeDetector> _logger;
    private readonly InferenceSession? _session;
    private readonly string _inputName = "images";
    private readonly IReadOnlyDictionary<int, DetectedClass> _classMap = new Dictionary<int, DetectedClass>();

    private readonly InferenceSession? _personSession;
    private readonly string _personInputName = "images";
    private readonly IReadOnlyDictionary<int, DetectedClass> _personClassMap = new Dictionary<int, DetectedClass>();

    private readonly InferenceSession? _maskSession;
    private readonly string _maskInputName = "pixel_values";
    private readonly string _maskOutputName = "logits";

    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }
    public bool IsFakeMode => false;

    // 세션 3개(PPE/Person/Mask)를 한 프로세스에서 돌리는데, 기본 설정대로면 세션마다 CPU 코어 수만큼
    // 스레드를 쓰려고 해서 서로(그리고 같은 컴퓨터에서 도는 클라이언트의 카메라 캡처/인코딩과도) CPU를
    // 다투게 된다. 실측 결과 프레임당 추론 소요가 1초 이상으로 늘어난 주된 원인이라, 세션당 스레드 수를
    // 제한해 경쟁을 줄인다.
    private static SessionOptions CreateSessionOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        InterOpNumThreads = 1,
    };

    public OnnxPpeDetector(SafetyVisionOptions options, ILogger<OnnxPpeDetector> logger)
    {
        _options = options;
        _logger = logger;

        string fullPath = Path.GetFullPath(options.ModelPath);
        if (!File.Exists(fullPath))
        {
            UnavailableReason = $"모델 파일을 찾을 수 없습니다: {fullPath}";
            _logger.LogError("{Reason}", UnavailableReason);
            return;
        }

        try
        {
            _session = new InferenceSession(fullPath, CreateSessionOptions());
            _inputName = _session.InputMetadata.Keys.First();

            if (!_session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var namesMetadata))
            {
                UnavailableReason = "모델 metadata에 names 필드가 없습니다.";
                _logger.LogError("{Reason}", UnavailableReason);
                _session.Dispose();
                _session = null;
                return;
            }

            if (!ModelClassMapParser.TryParse(namesMetadata, out var classMap, out var error))
            {
                UnavailableReason = error;
                _logger.LogError("{Reason}", UnavailableReason);
                _session.Dispose();
                _session = null;
                return;
            }

            _classMap = classMap;
            IsAvailable = true;
            _logger.LogInformation(
                "ONNX 모델 로드 완료: {Path}, 입력={Input}, 출력={Outputs}",
                fullPath, _inputName, string.Join(",", _session.OutputMetadata.Keys));
        }
        catch (Exception ex)
        {
            UnavailableReason = $"모델 로딩 실패: {ex.Message}";
            _logger.LogError(ex, "ONNX 모델 로딩 실패");
            return;
        }

        (_personSession, _personInputName, _personClassMap) = TryLoadPersonModel(options.PersonModelPath);
        (_maskSession, _maskInputName, _maskOutputName) = TryLoadMaskModel(options.MaskModelPath);
    }

    private (InferenceSession? Session, string InputName, IReadOnlyDictionary<int, DetectedClass> ClassMap) TryLoadPersonModel(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(fullPath))
        {
            _logger.LogWarning(
                "범용 Person 모델 파일을 찾을 수 없어 PPE 모델 자체의 Person 결과로 대체합니다: {Path}", fullPath);
            return (null, _inputName, new Dictionary<int, DetectedClass>());
        }

        try
        {
            var session = new InferenceSession(fullPath, CreateSessionOptions());
            string inputName = session.InputMetadata.Keys.First();

            if (!session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var namesMetadata)
                || !ModelClassMapParser.TryFindClassIndex(namesMetadata, "person", out int personIndex))
            {
                _logger.LogWarning(
                    "범용 Person 모델 metadata에서 'person' 클래스를 찾을 수 없어 사용하지 않습니다: {Path}", fullPath);
                session.Dispose();
                return (null, _inputName, new Dictionary<int, DetectedClass>());
            }

            _logger.LogInformation("범용 Person 모델 로드 완료: {Path}, 입력={Input}", fullPath, inputName);
            return (session, inputName, new Dictionary<int, DetectedClass> { [personIndex] = DetectedClass.Person });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "범용 Person 모델 로딩 실패, PPE 모델 자체의 Person 결과로 대체합니다: {Path}", fullPath);
            return (null, _inputName, new Dictionary<int, DetectedClass>());
        }
    }

    private (InferenceSession? Session, string InputName, string OutputName) TryLoadMaskModel(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(fullPath))
        {
            _logger.LogWarning(
                "Mask 분류기 모델 파일을 찾을 수 없어 Mask 판정 없이 진행합니다: {Path}", fullPath);
            return (null, _maskInputName, _maskOutputName);
        }

        try
        {
            var session = new InferenceSession(fullPath, CreateSessionOptions());
            string inputName = session.InputMetadata.Keys.First();
            string outputName = session.OutputMetadata.Keys.First();
            _logger.LogInformation("Mask 분류기 모델 로드 완료: {Path}, 입력={Input}, 출력={Output}", fullPath, inputName, outputName);
            return (session, inputName, outputName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mask 분류기 모델 로딩 실패, Mask 판정 없이 진행합니다: {Path}", fullPath);
            return (null, _maskInputName, _maskOutputName);
        }
    }

    // RGB Mat(연속 메모리, CV_8UC3 가정)을 한 번에 통째로 복사해 [1,3,size,size] NCHW 텐서로 채운다.
    // 픽셀 하나씩 Mat.At<Vec3b>()+Tensor 다중 인덱서를 호출하는 방식은 (특히 896x896처럼 픽셀이 많을 때)
    // 호출당 오버헤드가 커서 실측 결과 프레임 처리 속도의 실질적 병목이었다. 대량 메모리 복사 + flat 배열
    // 접근으로 바꿔 속도를 크게 개선한다.
    private static DenseTensor<float> BuildInputTensor(Mat rgb, int inputSize, bool signedNormalize)
    {
        int pixelCount = inputSize * inputSize;
        var pixelData = new byte[pixelCount * 3];
        Marshal.Copy(rgb.Data, pixelData, 0, pixelData.Length);

        var buffer = new float[3 * pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            int o = i * 3;
            float r = pixelData[o] / 255f;
            float g = pixelData[o + 1] / 255f;
            float b = pixelData[o + 2] / 255f;
            if (signedNormalize) { r = (r - 0.5f) / 0.5f; g = (g - 0.5f) / 0.5f; b = (b - 0.5f) / 0.5f; }
            buffer[i] = r;
            buffer[pixelCount + i] = g;
            buffer[2 * pixelCount + i] = b;
        }

        return new DenseTensor<float>(buffer, [1, 3, inputSize, inputSize]);
    }

    // 안전모 착용 여부를 PPE 모델의 Hardhat 신뢰도로 판단해서 크롭 기준점을 보정하려 했으나, 실측 결과
    // 안전모를 명백히 쓰고 있어도 특정 프레임에서는 신뢰도가 0.0003 수준(완전한 노이즈)까지 떨어지는
    // 경우가 있어 어떤 임계값을 쓰든 놓치는 프레임이 생긴다(별도 검증 완료). 그래서 별도 모델의 신뢰도에
    // 의존하지 않고, 지금 이 프레임의 실제 픽셀에서 "피부색이 시작되는 위치"를 직접 스캔해서 얼굴 시작점을
    // 찾는다 — 안전모를 쓰든 안 쓰든(머리카락/헬멧 모두 피부색이 아니므로) 항상 진짜 눈/이마 위치를
    // 기준으로 삼을 수 있다. 사람 박스 중앙의 세로 밴드를 위에서부터 스캔하며, 피부색 비율이 일정 구간
    // 이상 유지되는 첫 지점을 얼굴 시작점으로 본다. 뚜렷한 전환을 못 찾으면 기존처럼 사람 박스 맨 위를 쓴다.
    private static int FindFaceTop(Mat source, DetectedBox person)
    {
        int cx = (int)person.CenterX;
        int bandHalf = Math.Max(1, (int)(0.10 * person.Width));
        int top = Math.Clamp((int)person.Y, 0, source.Height - 1);
        int bottom = Math.Clamp((int)(person.Y + 0.45 * person.Height), top + 1, source.Height);
        int x1 = Math.Clamp(cx - bandHalf, 0, source.Width - 1);
        int x2 = Math.Clamp(cx + bandHalf, x1 + 1, source.Width);
        if (bottom - top < 5 || x2 - x1 < 1) return top;

        using var band = new Mat(source, new Rect(x1, top, x2 - x1, bottom - top));
        using var ycrcb = new Mat();
        Cv2.CvtColor(band, ycrcb, ColorConversionCodes.BGR2YCrCb);
        using var skinMask = new Mat();
        // OpenCV YCrCb 채널 순서는 (Y, Cr, Cb). 일반적인 피부색 범위 임계값을 사용한다.
        Cv2.InRange(ycrcb, new Scalar(40, 135, 85), new Scalar(255, 180, 135), skinMask);

        const int window = 5;
        const double skinRatioThreshold = 0.35;
        int rows = skinMask.Rows, cols = skinMask.Cols;

        // Mat.Row()를 반복 호출하면 매번 서브 Mat을 새로 할당한다(사람마다, 프레임마다 반복되는 호출이라
        // 다른 곳과 동일하게 벌크 복사로 피한다). skinMask는 연속 메모리의 CV_8UC1이므로 한 번에 복사한다.
        var mask = new byte[rows * cols];
        Marshal.Copy(skinMask.Data, mask, 0, mask.Length);

        var rowRatios = new double[rows];
        for (int r = 0; r < rows; r++)
        {
            int count = 0;
            int offset = r * cols;
            for (int c = 0; c < cols; c++)
                if (mask[offset + c] != 0) count++;
            rowRatios[r] = count / (double)cols;
        }

        for (int r = 0; r <= rows - window; r++)
        {
            double avg = 0;
            for (int w = 0; w < window; w++) avg += rowRatios[r + w];
            if (avg / window > skinRatioThreshold) return top + r;
        }
        return top;
    }

    // safetyvision 모델의 Mask/NO-Mask 클래스는 실측 결과 신뢰도가 사실상 0에 가까워(별도 검증 완료) 대신
    // 사람 박스마다 얼굴 영역을 잘라 이진 분류기(Mask/No Mask)에 넣는다. 결과는 Hardhat/SafetyVest와 동일하게
    // 합성 DetectedBox로 만들어 FrameAnalyzer의 기존 연관 판정 로직을 그대로 재사용한다.
    private DetectedBox? ClassifyMask(Mat source, DetectedBox person, int faceTop, double faceCenterX)
    {
        if (_maskSession is null) return null;

        // 눈 위쪽에서 시작하던 크롭을 코·입·턱 방향으로 조금 내려 마스크 착용 부위를 중심에 둔다.
        double headTop = Math.Max(person.Y, faceTop)
            + _options.MaskClassifierTopOffsetRatio * person.Height;
        double cropHalfWidth = _options.MaskClassifierCropHalfWidthRatio * person.Width;
        double cx = faceCenterX;
        int x = (int)Math.Clamp(cx - cropHalfWidth, 0, source.Width);
        int y = (int)Math.Clamp(headTop, 0, source.Height);
        int x2 = (int)Math.Clamp(cx + cropHalfWidth, 0, source.Width);
        int y2 = (int)Math.Clamp(headTop + _options.MaskClassifierCropBottomRatio * person.Height, 0, source.Height);
        int w = x2 - x, h = y2 - y;
        if (w <= 0 || h <= 0) return null;

        int inputSize = _options.MaskModelInputSize;
        using var face = new Mat(source, new Rect(x, y, w, h));
        using var resized = new Mat();
        Cv2.Resize(face, resized, new Size(inputSize, inputSize));
        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        var input = BuildInputTensor(rgb, inputSize, signedNormalize: true);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_maskInputName, input) };
        using var results = _maskSession.Run(inputs);
        var logits = results.First(r => r.Name == _maskOutputName).AsEnumerable<float>().ToArray();
        if (logits.Length < 2) return null;

        // 이 ViT 분류기의 학습 label 순서는 index 0 = Mask, index 1 = No Mask다.
        // ONNX 파일에는 id2label metadata가 보존되지 않으므로 원본 모델의 config 순서를 따른다.
        float max = Math.Max(logits[0], logits[1]);
        float e0 = MathF.Exp(logits[0] - max), e1 = MathF.Exp(logits[1] - max);
        float pMask = e0 / (e0 + e1), pNoMask = e1 / (e0 + e1);

        var (detectedClass, confidence) = pMask >= pNoMask ? (DetectedClass.Mask, pMask) : (DetectedClass.NoMask, pNoMask);
        if (confidence < _options.MaskClassifierConfidence) return null;

        return new DetectedBox(detectedClass, x, y, w, h, confidence);
    }

    private DetectedBox? FindHeadwear(
        DetectedBox person,
        IReadOnlyList<DetectedBox> ppeBoxes)
    {
        return ppeBoxes
            .Where(b => b.Class is DetectedClass.Hardhat or DetectedClass.NoHardhat)
            .Where(b => PpeAssociationRules.IsCandidate(b.Class, person, b, _options))
            .OrderByDescending(b => b.Confidence)
            .Select(b => (DetectedBox?)b)
            .FirstOrDefault();
    }

    private static int AdjustFaceTopForHeadwear(DetectedBox headwear, int detectedFaceTop)
    {
        double anchorRatio = headwear.Class switch
        {
            // 안전모 박스는 헬멧 본체가 대부분이므로 하단 가까이에서 얼굴을 시작한다.
            DetectedClass.Hardhat => 0.75,
            // NO-Hardhat 박스는 머리카락부터 턱까지 포함하므로 상단 35%를 제외한다.
            DetectedClass.NoHardhat => 0.35,
            _ => 0,
        };
        int anchoredTop = (int)(headwear.Y + headwear.Height * anchorRatio);
        return Math.Max(detectedFaceTop, anchoredTop);
    }

    public DetectionFrame Detect(ReadOnlySpan<byte> jpegBytes)
    {
        if (!IsAvailable || _session is null)
            throw new InvalidOperationException(UnavailableReason ?? "모델을 사용할 수 없습니다.");

        using var source = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        if (source.Empty()) throw new InvalidDataException("이미지를 디코딩할 수 없습니다.");

        var ppeBoxes = RunModel(source, _session, _inputName, _options.ModelInputSize, _classMap,
            c => c switch
            {
                DetectedClass.NoHardhat or DetectedClass.NoSafetyVest => (float)_options.NoWearDetectionConfidence,
                DetectedClass.Hardhat => (float)_options.HardhatDetectionConfidence,
                _ => (float)_options.DetectionConfidence,
            });

        // safetyvision 모델 자체의 Person/Mask/NO-Mask 결과는 버린다(신뢰도 검증 실패, 사실상 0에 가까움).
        // Hardhat/NoHardhat/SafetyVest/NoSafetyVest만 이 모델 결과를 그대로 쓴다.
        var boxes = ppeBoxes
            .Where(b => b.Class is not (DetectedClass.Person or DetectedClass.Mask or DetectedClass.NoMask))
            .ToList();

        List<DetectedBox> personBoxes;
        if (_personSession is not null)
        {
            personBoxes = RunModel(source, _personSession, _personInputName, _options.PersonModelInputSize,
                _personClassMap, _ => (float)_options.PersonDetectionConfidence);
        }
        else
        {
            personBoxes = ppeBoxes.Where(b => b.Class == DetectedClass.Person).ToList();
        }

        boxes.AddRange(personBoxes);

        // 사람 박스마다 얼굴 영역을 잘라 Mask 분류기를 돌리고, 결과를 합성 DetectedBox로 추가한다.
        foreach (var person in personBoxes)
        {
            var headwear = FindHeadwear(person, ppeBoxes);
            // 사람 전체 박스만으로 얼굴 위치를 추측하면 얼굴이 화면 밖에 있거나 몸을 기울인 장면에서
            // 팔·옷·안전모를 얼굴로 잘라 마스크로 오판한다. 머리 위치를 확인한 사람만 분류한다.
            if (headwear is null)
                continue;

            int faceTop = FindFaceTop(source, person);
            faceTop = AdjustFaceTopForHeadwear(headwear.Value, faceTop);
            double faceCenterX = headwear.Value.CenterX;
            var maskBox = ClassifyMask(source, person, faceTop, faceCenterX);
            if (maskBox is not null) boxes.Add(maskBox.Value);
        }

        return new DetectionFrame(boxes, source.Width, source.Height);
    }

    // letterbox → 전처리 → 추론 → 클래스별 최고점 선택 → 임계값 필터 → NMS → 원본 좌표 역변환까지
    // 한 모델에 대해 전부 수행한다. PPE 모델과 범용 Person 모델 양쪽에서 동일하게 사용한다.
    private List<DetectedBox> RunModel(
        Mat source,
        InferenceSession session,
        string inputName,
        int inputSize,
        IReadOnlyDictionary<int, DetectedClass> classMap,
        Func<DetectedClass, float> thresholdFor)
    {
        var letterbox = Letterbox.Apply(source, inputSize);
        using var canvas = letterbox.Image;
        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);

        var input = BuildInputTensor(rgb, inputSize, signedNormalize: false);
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, input) };
        using var results = session.Run(inputs);
        var output = results.First().AsTensor<float>();

        var dims = output.Dimensions;
        if (dims.Length != 3 || dims[0] != 1)
            throw new InvalidDataException($"예상하지 못한 모델 출력 형태입니다: [{string.Join(",", dims.ToArray())}]");

        int numChannels = dims[1];
        int numBoxes = dims[2];
        int numClasses = numChannels - 4;
        if (numClasses <= 0)
            throw new InvalidDataException($"예상하지 못한 모델 출력 채널 수입니다: {numChannels}");

        // output[0, c, i]를 다중 인덱서로 매번 호출하면(내부적으로 인덱스 배열을 새로 할당함) 박스 수가
        // 많은 모델(특히 COCO 80클래스 Person 모델)에서 병목이 된다. [1,numChannels,numBoxes]의 연속
        // 메모리를 직접 flat하게 읽어(offset = c*numBoxes + i) 같은 값을 훨씬 빠르게 얻는다.
        var span = ((DenseTensor<float>)output).Buffer.Span;

        var raw = new List<RawDetection>();
        for (int i = 0; i < numBoxes; i++)
        {
            float cx = span[i];
            float cy = span[numBoxes + i];
            float w = span[2 * numBoxes + i];
            float h = span[3 * numBoxes + i];

            int bestClass = -1;
            float bestScore = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                float score = span[(4 + c) * numBoxes + i];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestClass < 0)
                continue;

            if (!classMap.TryGetValue(bestClass, out var detectedClass))
                continue;

            if (bestScore < thresholdFor(detectedClass))
                continue;

            raw.Add(new RawDetection(detectedClass, bestScore, cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2));
        }

        var afterNms = NonMaxSuppression.ApplyPerClass(raw, _options.NmsIouThreshold);

        var boxes = new List<DetectedBox>(afterNms.Count);
        foreach (var d in afterNms)
        {
            var (x1, y1) = Letterbox.InverseTransform(d.X1, d.Y1, letterbox);
            var (x2, y2) = Letterbox.InverseTransform(d.X2, d.Y2, letterbox);
            x1 = Math.Clamp(x1, 0, source.Width);
            y1 = Math.Clamp(y1, 0, source.Height);
            x2 = Math.Clamp(x2, 0, source.Width);
            y2 = Math.Clamp(y2, 0, source.Height);
            boxes.Add(new DetectedBox(d.Class, x1, y1, x2 - x1, y2 - y1, d.Confidence));
        }

        return boxes;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _personSession?.Dispose();
        _maskSession?.Dispose();
    }
}
