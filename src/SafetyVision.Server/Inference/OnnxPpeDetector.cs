using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

// PPE 전용 모델과 범용 Person 모델의 생명주기를 관리하고 두 모델의 결과를 조합한다.
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
            _logger.LogInformation("ONNX 모델 로드 완료: {Path}, 입력={Input}, 출력={Outputs}",
                fullPath, _inputName, string.Join(",", _session.OutputMetadata.Keys));
        }
        catch (Exception ex)
        {
            UnavailableReason = $"모델 로딩 실패: {ex.Message}";
            _logger.LogError(ex, "ONNX 모델 로딩 실패");
            return;
        }

        (_personSession, _personInputName, _personClassMap) = TryLoadPersonModel(options.PersonModelPath);
    }

    private (InferenceSession? Session, string InputName, IReadOnlyDictionary<int, DetectedClass> ClassMap)
        TryLoadPersonModel(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(fullPath))
        {
            _logger.LogWarning("범용 Person 모델 파일을 찾을 수 없어 PPE 모델 자체의 Person 결과로 대체합니다: {Path}", fullPath);
            return (null, _inputName, new Dictionary<int, DetectedClass>());
        }

        try
        {
            var session = new InferenceSession(fullPath, CreateSessionOptions());
            string inputName = session.InputMetadata.Keys.First();
            if (!session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var namesMetadata)
                || !ModelClassMapParser.TryFindClassIndex(namesMetadata, "person", out int personIndex))
            {
                _logger.LogWarning("범용 Person 모델 metadata에서 'person' 클래스를 찾을 수 없어 사용하지 않습니다: {Path}", fullPath);
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

    public DetectionFrame Detect(ReadOnlySpan<byte> jpegBytes)
    {
        if (!IsAvailable || _session is null)
            throw new InvalidOperationException(UnavailableReason ?? "모델을 사용할 수 없습니다.");

        using var source = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
        if (source.Empty()) throw new InvalidDataException("이미지를 디코딩할 수 없습니다.");

        var ppeBoxes = YoloModelRunner.Run(source, _session, _inputName, _options.ModelInputSize, _classMap,
            c => c switch
            {
                DetectedClass.NoHardhat or DetectedClass.NoSafetyVest => (float)_options.NoWearDetectionConfidence,
                DetectedClass.Hardhat => (float)_options.HardhatDetectionConfidence,
                DetectedClass.Mask or DetectedClass.NoMask => (float)_options.MaskDetectionConfidence,
                _ => (float)_options.DetectionConfidence,
            }, _options.NmsIouThreshold);

        var personBoxes = _personSession is not null
            ? YoloModelRunner.Run(source, _personSession, _personInputName, _options.PersonModelInputSize,
                _personClassMap, _ => (float)_options.PersonDetectionConfidence, _options.NmsIouThreshold)
            : ppeBoxes.Where(b => b.Class == DetectedClass.Person).ToList();

        var boxes = PpeDetectionResolver.Resolve(personBoxes, ppeBoxes, _options);
        return new DetectionFrame(boxes, source.Width, source.Height);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _personSession?.Dispose();
    }
}
