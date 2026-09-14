using Microsoft.Extensions.Logging.Abstractions;
using SafetyVision.Core.Analysis;
using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;
using SafetyVision.Server.Inference;

var repo = FindRepositoryRoot(AppContext.BaseDirectory);
var source = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SafetyVision", "snapshots", "20260914");
var expected = new Dictionary<int, Dictionary<EquipmentCode, FrameVote>>
{
    [229] = E(true, false, false),
    [230] = E(true, false, false),
    [254] = E(true, false, false),
    [255] = E(true, false, false),
    [256] = E(true, false, true),
    [257] = E(true, false, true),
    [258] = E(true, false, true),
    [271] = E(true, true, false),
    [272] = E(true, true, false),
};

var options = new SafetyVisionOptions
{
    ModelPath = Path.Combine(repo, "src", "SafetyVision.Server", "models", "safetyvision_v2_896.onnx"),
    ModelInputSize = 896,
    PersonModelPath = Path.Combine(repo, "src", "SafetyVision.Server", "models", "yolov8n.onnx"),
    PersonModelInputSize = 640,
    PersonDetectionConfidence = .40,
    DetectionConfidence = .40,
    NoWearDetectionConfidence = .05,
    HardhatDetectionConfidence = .01,
    MaskDetectionConfidence = .00001,
    HeadTopMarginRatio = .15,
    MinPersonHeightRatio = .40,
    MaxPersonHeightRatio = .99,
    MaxPersonWidthToHeightRatio = .75,
};

using var detector = new OnnxPpeDetector(options, NullLogger<OnnxPpeDetector>.Instance);
int correct = 0, total = 0, allCorrect = 0;
foreach (var (id, truth) in expected)
{
    var path = Path.Combine(source, $"inspection_{id}.jpg");
    var detection = detector.Detect(await File.ReadAllBytesAsync(path));
    Console.WriteLine("  persons: " + string.Join("; ", detection.Boxes.Where(b => b.Class == DetectedClass.Person).Select(b => $"{b.Confidence:F2}@({b.X:F0},{b.Y:F0},{b.Width:F0},{b.Height:F0}) ar={b.Width / b.Height:F2}")));
    Console.WriteLine("  ppe: " + string.Join("; ", detection.Boxes.Where(b => b.Class != DetectedClass.Person).Select(b => $"{b.Class}:{b.Confidence:F5}@({b.X:F0},{b.Y:F0},{b.Width:F0},{b.Height:F0})")));
    var evaluation = FrameAnalyzer.Evaluate(detection.Boxes, detection.Width, detection.Height, options);
    var parts = new List<string>();
    bool photoCorrect = evaluation.Condition == PersonRoiCondition.Qualified;
    foreach (var equipment in Enum.GetValues<EquipmentCode>())
    {
        var actual = evaluation.Votes.GetValueOrDefault(equipment, FrameVote.NoInfo);
        var ok = actual == truth[equipment];
        correct += ok ? 1 : 0;
        total++;
        photoCorrect &= ok;
        parts.Add($"{equipment}={actual}/{truth[equipment]}{(ok ? "" : "*")}");
    }
    allCorrect += photoCorrect ? 1 : 0;
    Console.WriteLine($"{id}: {evaluation.Condition}; {string.Join(", ", parts)}");
}
Console.WriteLine($"EQUIPMENT_ACCURACY={(double)correct / total:P2} ({correct}/{total})");
Console.WriteLine($"PHOTO_ACCURACY={(double)allCorrect / expected.Count:P2} ({allCorrect}/{expected.Count})");
return (double)correct / total >= 0.90 && (double)allCorrect / expected.Count >= 0.90 ? 0 : 1;

static Dictionary<EquipmentCode, FrameVote> E(bool hardhat, bool vest, bool mask) => new()
{
    [EquipmentCode.Hardhat] = hardhat ? FrameVote.Positive : FrameVote.Negative,
    [EquipmentCode.Vest] = vest ? FrameVote.Positive : FrameVote.Negative,
    [EquipmentCode.Mask] = mask ? FrameVote.Positive : FrameVote.Negative,
};

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SafetyVision.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("SafetyVision 저장소를 찾지 못했습니다.");
}
