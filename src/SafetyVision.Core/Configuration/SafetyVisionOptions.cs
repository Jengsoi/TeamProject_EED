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

    // safetyvision 모델의 Mask/NO-Mask 클래스도 Person과 마찬가지로 실측 결과 신뢰도가 사실상 0에 가까워
    // (동일 이미지에서 raw score 0.001 미만, 별도 검증 완료), 탐지 대신 얼굴 영역 분류기로 대체한다.
    // 사람 박스마다 머리 위쪽 기준 (가로 MaskClassifierCropHalfWidthRatio*2, 세로 MaskClassifierCropBottomRatio)
    // 만큼 잘라 분류기에 넣고, Hardhat/SafetyVest와 동일한 방식(합성 DetectedBox)으로 FrameAnalyzer에 넘긴다.
    public string MaskModelPath { get; set; } = "models/mask_classifier.onnx";
    public int MaskModelInputSize { get; set; } = 224;
    public double MaskClassifierTopOffsetRatio { get; set; } = 0.065;

    // 최근 대표 이미지 기준으로 눈 위주였던 영역을 코·입·턱까지 포함하도록 높이를 0.23으로 확장했다.
    // 시작점은 Hardhat/NO-Hardhat 박스를 기준으로 별도 보정하므로 헬멧이나 어깨가 섞이지 않게 한다.
    public double MaskClassifierCropBottomRatio { get; set; } = 0.23;
    public double MaskClassifierCropHalfWidthRatio { get; set; } = 0.20;
    public double MaskClassifierConfidence { get; set; } = 0.60;

    public double DetectionConfidence { get; set; } = 0.40;

    // "미착용"(NO-Hardhat/NO-Safety Vest) 클래스는 실측 결과 신뢰도가 전반적으로 낮다(0.01~0.23대).
    // 반면 실제로 착용 중일 때 이 클래스들의 점수는 0.0005 이하로 매우 깨끗하게 낮으므로(별도 검증 완료),
    // 오탐 여유를 확보하면서 미착용 클래스만 더 낮은 임계값을 적용한다.
    public double NoWearDetectionConfidence { get; set; } = 0.05;

    // Hardhat(착용) 클래스는 대부분 0.7~1.0대로 신뢰도가 높지만, 특정 프레임(각도/조명)에서는 안전모를
    // 명백히 쓰고 있어도 0.0003~0.13까지 떨어지는 경우가 실측으로 확인됐다. 반대로 안전모가 없을 때
    // 이 클래스의 점수는 0.001 이하로 매우 깨끗하게 낮으므로(별도 검증 완료), NO-Hardhat/NO-Safety Vest와
    // 같은 이유로 Hardhat도 SafetyVest와 별도로 더 낮은 임계값을 쓴다(SafetyVest는 0.40에서도 안정적이라
    // DetectionConfidence를 그대로 쓴다).
    public double HardhatDetectionConfidence { get; set; } = 0.15;
    public double MaskDetectionConfidence { get; set; } = 0.001;

    public double NmsIouThreshold { get; set; } = 0.45;

    public double RoiLeft { get; set; } = 0.20;
    public double RoiTop { get; set; } = 0.05;
    public double RoiRight { get; set; } = 0.80;
    public double RoiBottom { get; set; } = 0.95;

    public double MinPersonHeightRatio { get; set; } = 0.40;
    public double MaxPersonHeightRatio { get; set; } = 0.92;
    public double MaxPersonWidthToHeightRatio { get; set; } = 0.75;
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
    public int ListenPort { get; set; } = 8910;

    public ConnectionStringsOptions ConnectionStrings { get; set; } = new();

    public IEnumerable<string> Validate()
    {
        if (ModelInputSize <= 0 || ModelInputSize % 32 != 0) yield return "ModelInputSize는 32의 배수인 양수여야 합니다.";
        if (PersonModelInputSize <= 0 || PersonModelInputSize % 32 != 0) yield return "PersonModelInputSize는 32의 배수인 양수여야 합니다.";
        if (PersonDetectionConfidence is < 0 or > 1) yield return "PersonDetectionConfidence는 0~1 사이여야 합니다.";
        if (MaskModelInputSize <= 0) yield return "MaskModelInputSize는 양수여야 합니다.";
        if (MaskClassifierTopOffsetRatio is < 0 or > 0.15) yield return "MaskClassifierTopOffsetRatio는 0~0.15 사이여야 합니다.";
        if (MaskClassifierCropBottomRatio <= 0) yield return "MaskClassifierCropBottomRatio는 양수여야 합니다.";
        if (MaskClassifierCropHalfWidthRatio <= 0) yield return "MaskClassifierCropHalfWidthRatio는 양수여야 합니다.";
        if (MaskClassifierConfidence is <= 0.5 or > 1) yield return "MaskClassifierConfidence는 0.5 초과 1 이하여야 합니다.";
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
        if (DecisionRatio is <= 0.5 or > 1) yield return "DecisionRatio는 0.5 초과 1 이하여야 합니다.";
        if (JpegQuality is < 1 or > 100) yield return "JpegQuality는 1~100 사이여야 합니다.";
        if (ListenPort is <= 0 or > 65535) yield return "ListenPort는 1~65535 사이여야 합니다.";
        if (string.IsNullOrWhiteSpace(ConnectionStrings.MySql)) yield return "ConnectionStrings:MySql이 비어 있습니다.";
    }
}

public sealed class ConnectionStringsOptions
{
    public string MySql { get; set; } = "";
}
