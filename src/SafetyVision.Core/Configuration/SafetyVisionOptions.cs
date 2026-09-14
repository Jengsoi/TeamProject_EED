using System.Data.Common;
using System.Net;

namespace SafetyVision.Core.Configuration;

public sealed class SafetyVisionOptions
{
    public int CameraIndex { get; set; } = 0;
    public int CameraWidth { get; set; } = 1280;
    public int CameraHeight { get; set; } = 720;

    public string ModelPath { get; set; } = "models/safetyvision_v2_640.onnx";
    public string ModelName { get; set; } = "ayushgupta7777/safetyvision-yolov8";
    public string ModelVersion { get; set; } = "v2";
    public bool UseFakeDetection { get; set; } = false;

    // 모델 입력 해상도(정사각형, letterbox 대상 크기). YOLO 구조상 32의 배수여야 한다.
    // 모델 파일을 다른 입력 해상도로 export된 버전으로 교체할 때 이 값도 함께 맞춰야 한다.
    public int ModelInputSize { get; set; } = 640;

    // safetyvision 전용 모델의 Person 클래스는 실측 결과 신뢰도가 사실상 0에 가까워(별도 검증 완료),
    // "사람이 있는가"는 검증된 범용 COCO 사전학습 모델(예: yolov8n)에게 맡기고 PPE 판정만 위 모델이 담당한다.
    // 이 모델을 못 불러오면 PPE 모델 자체의 Person 결과로 자동 폴백한다.
    public string PersonModelPath { get; set; } = "models/yolov8n.onnx";
    public int PersonModelInputSize { get; set; } = 640;
    public double PersonDetectionConfidence { get; set; } = 0.40;

    public double DetectionConfidence { get; set; } = 0.40;

    // "미착용"(NO-Hardhat/NO-Safety Vest) 클래스의 점수는 낮다. 단일 Person 보정 표본에서 0.005가
    // 현재 결과 중 미착용 근거를 가장 많이 유지했다. 실제 점검은 여러 프레임의 다수결로 확정한다.
    public double NoWearDetectionConfidence { get; set; } = 0.005;

    // Hardhat(착용) 클래스는 대부분 0.7~1.0대로 신뢰도가 높지만, 특정 프레임(각도/조명)에서는 안전모를
    // 명백히 쓰고 있어도 0.0003~0.13까지 떨어지는 경우가 실측으로 확인됐다. 단일 Person 13장에서는
    // 0.005가 착용 안전모를 모두 유지했고, 이 값보다 높이면 일부가 미확인이 됐다.
    public double HardhatDetectionConfidence { get; set; } = 0.005;
    // 이 PPE 모델의 Mask/NO-Mask 점수는 매우 작다. 단일 Person 38장 보정에서 0.00001이 실제 착용 근거를
    // 가장 많이 유지하면서 착용자를 NO-Mask로 오판하지 않았다. 이 값만으로 근거가 생기지 않으면 미확인이다.
    public double MaskDetectionConfidence { get; set; } = 0.00001;

    public double NmsIouThreshold { get; set; } = 0.45;

    public double RoiLeft { get; set; } = 0.20;
    public double RoiTop { get; set; } = 0.05;
    public double RoiRight { get; set; } = 0.80;
    public double RoiBottom { get; set; } = 0.95;

    // 상체만 보이는 검사도 허용하기 위해 인물 박스 높이 하한을 낮춘다.
    public double MinPersonHeightRatio { get; set; } = 0.20;
    public double MaxPersonHeightRatio { get; set; } = 0.99;
    // 상체 박스는 폭이 높이보다 넓을 수 있어 비율을 넉넉히 허용한다.
    public double MaxPersonWidthToHeightRatio { get; set; } = 1.5;
    public double PersonStableDurationSeconds { get; set; } = 0.8;
    public double PersonLeaveDurationSeconds { get; set; } = 1.0;

    public double MinAnalysisDurationSeconds { get; set; } = 2.0;
    public double MaxAnalysisDurationSeconds { get; set; } = 5.0;
    public int TargetAnalysisFrames { get; set; } = 12;
    public int MinAnalysisFrames { get; set; } = 5;
    public int MaxInferenceFps { get; set; } = 6;

    // 미확인은 판별이 불가능한 경우로만 좁힌다(팀 결정, 05_AI모델명세.md의 0.50/0.70과 다름).
    // 착용/미착용 근거가 max(MinEvidenceFrames, N*MinEvidenceRatio)개 이상이면 DecisionRatio 이상인 다수 쪽으로 판정한다.
    public int MinEvidenceFrames { get; set; } = 3;
    public double MinEvidenceRatio { get; set; } = 0.25;
    public double DecisionRatio { get; set; } = 0.50;

    public double PersonHorizontalMarginRatio { get; set; } = 0.05;
    public double HeadTopMarginRatio { get; set; } = 0.15;
    public double HardhatBottomRatio { get; set; } = 0.35;
    public double MaskBottomRatio { get; set; } = 0.40;
    public double VestTopRatio { get; set; } = 0.20;
    public double VestBottomRatio { get; set; } = 0.75;

    public int JpegQuality { get; set; } = 90;
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 8910;

    public ConnectionStringsOptions ConnectionStrings { get; set; } = new();

    public IEnumerable<string> Validate()
    {
        if (ModelInputSize <= 0 || ModelInputSize % 32 != 0) yield return "ModelInputSize는 32의 배수인 양수여야 합니다.";
        if (PersonModelInputSize <= 0 || PersonModelInputSize % 32 != 0) yield return "PersonModelInputSize는 32의 배수인 양수여야 합니다.";
        if (PersonDetectionConfidence is < 0 or > 1) yield return "PersonDetectionConfidence는 0~1 사이여야 합니다.";
        if (DetectionConfidence is < 0 or > 1) yield return "DetectionConfidence는 0~1 사이여야 합니다.";
        if (NoWearDetectionConfidence is < 0 or > 1) yield return "NoWearDetectionConfidence는 0~1 사이여야 합니다.";
        if (HardhatDetectionConfidence is < 0 or > 1) yield return "HardhatDetectionConfidence는 0~1 사이여야 합니다.";
        if (MaskDetectionConfidence is < 0 or > 1) yield return "MaskDetectionConfidence는 0~1 사이여야 합니다.";
        if (NmsIouThreshold is < 0 or > 1) yield return "NmsIouThreshold는 0~1 사이여야 합니다.";
        if (!(RoiLeft >= 0 && RoiLeft < RoiRight && RoiRight <= 1)) yield return "RoiLeft < RoiRight 이며 0~1 범위여야 합니다.";
        if (!(RoiTop >= 0 && RoiTop < RoiBottom && RoiBottom <= 1)) yield return "RoiTop < RoiBottom 이며 0~1 범위여야 합니다.";
        if (MinPersonHeightRatio is <= 0 or > 1) yield return "MinPersonHeightRatio는 0~1 사이여야 합니다.";
        if (MaxPersonHeightRatio is <= 0 or > 1) yield return "MaxPersonHeightRatio는 0~1 사이여야 합니다.";
        if (MaxPersonHeightRatio <= MinPersonHeightRatio) yield return "MaxPersonHeightRatio는 MinPersonHeightRatio보다 커야 합니다.";
        if (MaxPersonWidthToHeightRatio is <= 0 or > 2) yield return "MaxPersonWidthToHeightRatio는 0 초과 2 이하여야 합니다.";
        if (PersonStableDurationSeconds <= 0) yield return "PersonStableDurationSeconds는 양수여야 합니다.";
        if (PersonLeaveDurationSeconds <= 0) yield return "PersonLeaveDurationSeconds는 양수여야 합니다.";
        if (MinAnalysisDurationSeconds <= 0) yield return "MinAnalysisDurationSeconds는 양수여야 합니다.";
        if (MaxAnalysisDurationSeconds < MinAnalysisDurationSeconds) yield return "MaxAnalysisDurationSeconds는 MinAnalysisDurationSeconds 이상이어야 합니다.";
        if (TargetAnalysisFrames < MinAnalysisFrames) yield return "TargetAnalysisFrames는 MinAnalysisFrames 이상이어야 합니다.";
        if (MinAnalysisFrames <= 0) yield return "MinAnalysisFrames는 양수여야 합니다.";
        if (MaxInferenceFps <= 0) yield return "MaxInferenceFps는 양수여야 합니다.";
        if (MinEvidenceFrames <= 0) yield return "MinEvidenceFrames는 양수여야 합니다.";
        if (MinEvidenceRatio is <= 0 or > 1) yield return "MinEvidenceRatio는 0 초과 1 이하여야 합니다.";
        if (DecisionRatio is < 0.5 or > 1) yield return "DecisionRatio는 0.5 이상 1 이하여야 합니다.";
        if (JpegQuality is < 1 or > 100) yield return "JpegQuality는 1~100 사이여야 합니다.";
        if (!IPAddress.TryParse(ListenAddress, out _)) yield return "ListenAddress는 유효한 IP 주소여야 합니다.";
        if (ListenPort is <= 0 or > 65535) yield return "ListenPort는 1~65535 사이여야 합니다.";
        var connectionStringError = ValidateMySqlConnectionString(ConnectionStrings?.MySql);
        if (connectionStringError is not null) yield return connectionStringError;
    }

    private static string? ValidateMySqlConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "ConnectionStrings:MySql이 비어 있습니다.";

        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException ex)
        {
            return $"ConnectionStrings:MySql 형식이 잘못되었습니다: {ex.Message}";
        }

        bool hasPassword = builder.Keys
            .Cast<string>()
            .Where(key => string.Equals(key, "Password", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Pwd", StringComparison.OrdinalIgnoreCase))
            .Any(key => !string.IsNullOrWhiteSpace(builder[key]?.ToString()));

        return hasPassword
            ? null
            : "환경변수 ConnectionStrings__MySql로 비밀번호를 포함한 연결 문자열을 지정해야 합니다.";
    }
}

public sealed class ConnectionStringsOptions
{
    public string MySql { get; set; } = "";
}
