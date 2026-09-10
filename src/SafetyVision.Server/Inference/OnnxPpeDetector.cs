using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

public sealed class OnnxPpeDetector : IPpeDetector, IDisposable
{
    private readonly SafetyVisionOptions _options;
    private readonly ILogger<OnnxPpeDetector> _logger;
    private readonly InferenceSession? _session;
    private readonly string _inputName = "images";
    private readonly IReadOnlyDictionary<int, DetectedClass> _classMap = new Dictionary<int, DetectedClass>();

    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }
    public bool IsFakeMode => false;

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
            _session = new InferenceSession(fullPath);
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
        }
    }

    public DetectionFrame Detect(ReadOnlySpan<byte> jpegBytes)
    {
        if (!IsAvailable || _session is null)
            throw new InvalidOperationException(UnavailableReason ?? "모델을 사용할 수 없습니다.");

        using var source = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        if (source.Empty()) throw new InvalidDataException("이미지를 디코딩할 수 없습니다.");

        int origWidth = source.Width;
        int origHeight = source.Height;

        var letterbox = Letterbox.Apply(source);
        using var canvas = letterbox.Image;
        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);

        var input = new DenseTensor<float>([1, 3, Letterbox.TargetSize, Letterbox.TargetSize]);
        for (int y = 0; y < Letterbox.TargetSize; y++)
        {
            for (int x = 0; x < Letterbox.TargetSize; x++)
            {
                var px = rgb.At<Vec3b>(y, x);
                input[0, 0, y, x] = px.Item0 / 255f;
                input[0, 1, y, x] = px.Item1 / 255f;
                input[0, 2, y, x] = px.Item2 / 255f;
            }
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, input) };
        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();

        var dims = output.Dimensions;
        if (dims.Length != 3 || dims[0] != 1)
            throw new InvalidDataException($"예상하지 못한 모델 출력 형태입니다: [{string.Join(",", dims.ToArray())}]");

        int numChannels = dims[1];
        int numBoxes = dims[2];
        int numClasses = numChannels - 4;
        if (numClasses <= 0)
            throw new InvalidDataException($"예상하지 못한 모델 출력 채널 수입니다: {numChannels}");

        var raw = new List<RawDetection>();
        for (int i = 0; i < numBoxes; i++)
        {
            float cx = output[0, 0, i];
            float cy = output[0, 1, i];
            float w = output[0, 2, i];
            float h = output[0, 3, i];

            int bestClass = -1;
            float bestScore = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                float score = output[0, 4 + c, i];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = c;
                }
            }

            if (bestClass < 0 || bestScore < _options.DetectionConfidence) continue;
            if (!_classMap.TryGetValue(bestClass, out var detectedClass)) continue;

            raw.Add(new RawDetection(detectedClass, bestScore, cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2));
        }

        var afterNms = NonMaxSuppression.ApplyPerClass(raw, _options.NmsIouThreshold);

        var boxes = new List<DetectedBox>(afterNms.Count);
        foreach (var d in afterNms)
        {
            var (x1, y1) = Letterbox.InverseTransform(d.X1, d.Y1, letterbox);
            var (x2, y2) = Letterbox.InverseTransform(d.X2, d.Y2, letterbox);
            x1 = Math.Clamp(x1, 0, origWidth);
            y1 = Math.Clamp(y1, 0, origHeight);
            x2 = Math.Clamp(x2, 0, origWidth);
            y2 = Math.Clamp(y2, 0, origHeight);
            boxes.Add(new DetectedBox(d.Class, x1, y1, x2 - x1, y2 - y1, d.Confidence));
        }

        return new DetectionFrame(boxes, origWidth, origHeight);
    }

    public void Dispose() => _session?.Dispose();
}
