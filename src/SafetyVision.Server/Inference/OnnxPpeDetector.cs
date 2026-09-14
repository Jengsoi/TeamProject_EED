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

    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }
    public bool IsFakeMode => false;

    // 세션 2개(PPE/Person)를 한 프로세스에서 돌리는데, 기본 설정대로면 세션마다 CPU 코어 수만큼
    // 스레드를 쓰려고 해서 서로(그리고 같은 컴퓨터에서 도는 클라이언트의 카메라 캡처/인코딩과도) CPU를
    // 다투게 된다. 실측 결과 프레임당 추론 소요가 1초 이상으로 늘어난 주된 원인이라, 세션당 스레드 수를
    // 제한해 경쟁을 줄인다.
    private static SessionOptions CreateSessionOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        InterOpNumThreads = 1,
    };

    private static string? ResolveModelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathFullyQualified(path))
            return path;

        string baseDirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(baseDirectoryPath))
            return baseDirectoryPath;

        string currentDirectoryPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        if (File.Exists(currentDirectoryPath))
            return currentDirectoryPath;

        string sourceTreePath = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "src", "SafetyVision.Server", path));
        if (File.Exists(sourceTreePath))
            return sourceTreePath;

        return baseDirectoryPath;
    }

    public OnnxPpeDetector(SafetyVisionOptions options, ILogger<OnnxPpeDetector> logger)
    {
        _options = options;
        _logger = logger;

        string? fullPath = null;
        try
        {
            fullPath = ResolveModelPath(options.ModelPath);
            if (fullPath is null)
            {
                UnavailableReason = "모델 경로가 비어 있습니다.";
                _logger.LogError("{Reason}", UnavailableReason);
                return;
            }

            if (!File.Exists(fullPath))
            {
                UnavailableReason = $"모델 파일을 찾을 수 없습니다: {fullPath}";
                _logger.LogError("{Reason}", UnavailableReason);
                return;
            }

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
            _logger.LogError(ex, "ONNX 모델 로딩 실패: {Path}", fullPath ?? options.ModelPath);
            return;
        }

        (_personSession, _personInputName, _personClassMap) = TryLoadPersonModel(options.PersonModelPath);
    }

    private (InferenceSession? Session, string InputName, IReadOnlyDictionary<int, DetectedClass> ClassMap) TryLoadPersonModel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning(
                "PersonModelPath가 비어 있어 범용 Person 모델 없이 PPE 모델 자체의 Person 결과로 대체합니다.");
            return (null, _inputName, new Dictionary<int, DetectedClass>());
        }

        string? fullPath = null;
        try
        {
            fullPath = ResolveModelPath(path);
            if (fullPath is null || !File.Exists(fullPath))
            {
                _logger.LogWarning(
                    "범용 Person 모델 파일을 찾을 수 없어 PPE 모델 자체의 Person 결과로 대체합니다: {Path}",
                    fullPath ?? path);
                return (null, _inputName, new Dictionary<int, DetectedClass>());
            }

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
            _logger.LogWarning(ex, "범용 Person 모델 로딩 실패, PPE 모델 자체의 Person 결과로 대체합니다: {Path}", fullPath ?? path);
            return (null, _inputName, new Dictionary<int, DetectedClass>());
        }
    }

    // RGB Mat(연속 메모리, CV_8UC3 가정)을 한 번에 통째로 복사해 [1,3,size,size] NCHW 텐서로 채운다.
    // 픽셀 하나씩 Mat.At<Vec3b>()+Tensor 다중 인덱서를 호출하는 방식은 (특히 896x896처럼 픽셀이 많을 때)
    // 호출당 오버헤드가 커서 실측 결과 프레임 처리 속도의 실질적 병목이었다. 대량 메모리 복사 + flat 배열
    // 접근으로 바꿔 속도를 크게 개선한다.
    private static DenseTensor<float> BuildInputTensor(Mat rgb, int inputSize)
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
            buffer[i] = r;
            buffer[pixelCount + i] = g;
            buffer[2 * pixelCount + i] = b;
        }

        return new DenseTensor<float>(buffer, [1, 3, inputSize, inputSize]);
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
                DetectedClass.Mask or DetectedClass.NoMask => (float)_options.MaskDetectionConfidence,
                _ => (float)_options.DetectionConfidence,
            });

        // Person은 범용 모델 결과를 사용하고 PPE 장비 클래스는 모두 전용 모델 결과를 사용한다.
        var boxes = ppeBoxes
            .Where(b => b.Class != DetectedClass.Person)
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

        PpeConflictResolver.Resolve(personBoxes, ppeBoxes, boxes, _options);

        // 임계값이 낮은 Mask/NO-Mask는 배경에서도 작은 후보가 생길 수 있다. 판정에도 쓰이지 않는
        // 사람 비연결 PPE를 결과 이미지에 그리지 않도록 여기서 제거한다.
        boxes.RemoveAll(box => box.Class != DetectedClass.Person
            && !personBoxes.Any(person => PpeAssociationRules.IsCandidate(box.Class, person, box, _options)));
        boxes.AddRange(personBoxes);

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

        var input = BuildInputTensor(rgb, inputSize);
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

                // YOLO 출력은 클래스별 독립 점수다. Mask/NO-Mask 점수는 다른 PPE 클래스보다 작아서
                // 전체 클래스 1등만 선택하면 얼굴 후보가 대부분 사라진다. 두 클래스는 각자 임계값을
                // 넘는 순간 독립 후보로 보존하고, 사람별 충돌 해소 단계에서 최종 하나를 고른다.
                if (classMap.TryGetValue(c, out var maskClass)
                    && maskClass is DetectedClass.Mask or DetectedClass.NoMask
                    && score >= thresholdFor(maskClass))
                {
                    raw.Add(new RawDetection(maskClass, score,
                        cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2));
                }

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

            // 위에서 클래스별로 추가했으므로 같은 후보를 다시 넣지 않는다.
            if (detectedClass is DetectedClass.Mask or DetectedClass.NoMask)
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
    }
}
