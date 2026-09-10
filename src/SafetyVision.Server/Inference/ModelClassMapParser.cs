using System.Text.RegularExpressions;
using SafetyVision.Core.Domain;

namespace SafetyVision.Server.Inference;

// 05_AI모델명세.md 2절: 모델 metadata의 실제 이름→ID 매핑을 사용한다. 추측 하드코딩 금지.
// Ultralytics YOLO ONNX export는 metadata의 "names" 키에 "{0: 'Hardhat', 1: '...', ...}" 형태 문자열을 담는다.
public static partial class ModelClassMapParser
{
    private static readonly Dictionary<string, DetectedClass> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Person"] = DetectedClass.Person,
        ["Hardhat"] = DetectedClass.Hardhat,
        ["NO-Hardhat"] = DetectedClass.NoHardhat,
        ["Mask"] = DetectedClass.Mask,
        ["NO-Mask"] = DetectedClass.NoMask,
        ["Safety Vest"] = DetectedClass.SafetyVest,
        ["NO-Safety Vest"] = DetectedClass.NoSafetyVest,
    };

    [GeneratedRegex(@"(\d+)\s*:\s*'([^']*)'")]
    private static partial Regex NamesEntryRegex();

    // key: 모델 클래스 인덱스, value: 우리가 사용하는 7개 클래스 (그 외는 매핑에서 제외됨).
    public static bool TryParse(string namesMetadata, out IReadOnlyDictionary<int, DetectedClass> classMap, out string? error)
    {
        var map = new Dictionary<int, DetectedClass>();
        var matches = NamesEntryRegex().Matches(namesMetadata);
        if (matches.Count == 0)
        {
            classMap = map;
            error = "모델 metadata의 names 필드를 해석할 수 없습니다.";
            return false;
        }

        foreach (Match m in matches)
        {
            int index = int.Parse(m.Groups[1].Value);
            string name = m.Groups[2].Value;
            if (KnownNames.TryGetValue(name, out var detectedClass))
                map[index] = detectedClass;
        }

        var missing = KnownNames.Keys.Where(name => !map.Values.Contains(KnownNames[name])).ToList();
        if (missing.Count > 0)
        {
            classMap = map;
            error = $"모델 metadata에서 다음 클래스를 찾을 수 없습니다: {string.Join(", ", missing)}";
            return false;
        }

        classMap = map;
        error = null;
        return true;
    }
}
