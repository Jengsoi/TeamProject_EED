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

    public double DetectionConfidence { get; set; } = 0.40;
    public double NmsIouThreshold { get; set; } = 0.45;

    public double RoiLeft { get; set; } = 0.20;
    public double RoiTop { get; set; } = 0.05;
    public double RoiRight { get; set; } = 0.80;
    public double RoiBottom { get; set; } = 0.95;

    public double MinPersonHeightRatio { get; set; } = 0.40;
    public double PersonStableDurationSeconds { get; set; } = 0.8;
    public double PersonLeaveDurationSeconds { get; set; } = 1.0;

    public double MinAnalysisDurationSeconds { get; set; } = 2.0;
    public double MaxAnalysisDurationSeconds { get; set; } = 5.0;
    public int TargetAnalysisFrames { get; set; } = 12;
    public int MinAnalysisFrames { get; set; } = 5;
    public int MaxInferenceFps { get; set; } = 6;

    public int MinEvidenceFrames { get; set; } = 3;
    public double MinEvidenceRatio { get; set; } = 0.50;
    public double DecisionRatio { get; set; } = 0.70;

    public double PersonHorizontalMarginRatio { get; set; } = 0.05;
    public double HeadTopMarginRatio { get; set; } = 0.05;
    public double HardhatBottomRatio { get; set; } = 0.35;
    public double MaskBottomRatio { get; set; } = 0.40;
    public double VestTopRatio { get; set; } = 0.20;
    public double VestBottomRatio { get; set; } = 0.75;

    public int JpegQuality { get; set; } = 90;

    public ConnectionStringsOptions ConnectionStrings { get; set; } = new();

    public IEnumerable<string> Validate()
    {
        if (DetectionConfidence is < 0 or > 1) yield return "DetectionConfidence는 0~1 사이여야 합니다.";
        if (NmsIouThreshold is < 0 or > 1) yield return "NmsIouThreshold는 0~1 사이여야 합니다.";
        if (!(RoiLeft >= 0 && RoiLeft < RoiRight && RoiRight <= 1)) yield return "RoiLeft < RoiRight 이며 0~1 범위여야 합니다.";
        if (!(RoiTop >= 0 && RoiTop < RoiBottom && RoiBottom <= 1)) yield return "RoiTop < RoiBottom 이며 0~1 범위여야 합니다.";
        if (MinPersonHeightRatio is <= 0 or > 1) yield return "MinPersonHeightRatio는 0~1 사이여야 합니다.";
        if (PersonStableDurationSeconds <= 0) yield return "PersonStableDurationSeconds는 양수여야 합니다.";
        if (PersonLeaveDurationSeconds <= 0) yield return "PersonLeaveDurationSeconds는 양수여야 합니다.";
        if (MinAnalysisDurationSeconds <= 0) yield return "MinAnalysisDurationSeconds는 양수여야 합니다.";
        if (MaxAnalysisDurationSeconds < MinAnalysisDurationSeconds) yield return "MaxAnalysisDurationSeconds는 MinAnalysisDurationSeconds 이상이어야 합니다.";
        if (TargetAnalysisFrames < MinAnalysisFrames) yield return "TargetAnalysisFrames는 MinAnalysisFrames 이상이어야 합니다.";
        if (MinAnalysisFrames <= 0) yield return "MinAnalysisFrames는 양수여야 합니다.";
        if (MaxInferenceFps <= 0) yield return "MaxInferenceFps는 양수여야 합니다.";
        if (MinEvidenceFrames <= 0) yield return "MinEvidenceFrames는 양수여야 합니다.";
        if (MinEvidenceRatio is <= 0 or > 1) yield return "MinEvidenceRatio는 0 초과 1 이하여야 합니다.";
        if (DecisionRatio is <= 0.5 or > 1) yield return "DecisionRatio는 0.5 초과 1 이하여야 합니다.";
        if (JpegQuality is < 1 or > 100) yield return "JpegQuality는 1~100 사이여야 합니다.";
        if (string.IsNullOrWhiteSpace(ConnectionStrings.MySql)) yield return "ConnectionStrings:MySql이 비어 있습니다.";
    }
}

public sealed class ConnectionStringsOptions
{
    public string MySql { get; set; } = "";
}
