using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using SafetyVision.Core.Configuration;
using SafetyVision.Protocol.Dto;
using SafetyVision.Server.Inference;

namespace SafetyVision.Server.Features;

// 시스템 설정은 조회 전용이며, 실제 수치 변경은 서버 appsettings.json에서 수행한다.
public sealed class SystemSettingsService(SafetyVisionOptions options, IPpeDetector detector)
{
    public SystemSettingsResponsePayload Handle(SystemSettingsRequestPayload request, bool databaseReady)
    {
        try
        {
            var database = ReadNonSecretDatabaseInfo(options.ConnectionStrings?.MySql);
            bool detectorAvailable = detector.IsAvailable;
            bool useFakeDetection = options.UseFakeDetection || detector.IsFakeMode;

            return new SystemSettingsResponsePayload(
                GetServerVersion(),
                GetServerStartedAtUtc(),
                options.ListenPort,
                databaseReady,
                database.Host,
                database.Name,
                options.ModelName ?? string.Empty,
                options.ModelVersion ?? string.Empty,
                options.ModelPath ?? string.Empty,
                detectorAvailable,
                detectorAvailable ? null : detector.UnavailableReason ?? "검출기를 사용할 수 없습니다.",
                useFakeDetection,
                options.UsePpeAsPersonProxy,
                CreateGroups());
        }
        catch (Exception)
        {
            // 상태 조회가 서버 요청 처리를 중단시키지 않도록 최소 정보만 반환한다.
            return new SystemSettingsResponsePayload(
                GetServerVersion(),
                GetServerStartedAtUtc(),
                0,
                databaseReady,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                "시스템 상태를 읽지 못했습니다.",
                false,
                false,
                []);
        }
    }

    private IReadOnlyList<SettingGroupPayload> CreateGroups() =>
    [
        new SettingGroupPayload("검출",
        [
            Item(nameof(options.DetectionConfidence), FormatRatio(options.DetectionConfidence), "검출 결과를 유효한 후보로 인정하는 최소 신뢰도입니다."),
            Item(nameof(options.NmsIouThreshold), FormatRatio(options.NmsIouThreshold), "겹치는 검출 상자를 하나로 정리하는 NMS IoU 기준입니다.")
        ]),
        new SettingGroupPayload("ROIㆍ인원",
        [
            Item(nameof(options.RoiLeft), FormatRatio(options.RoiLeft), "분석 ROI의 왼쪽 경계 비율입니다."),
            Item(nameof(options.RoiTop), FormatRatio(options.RoiTop), "분석 ROI의 위쪽 경계 비율입니다."),
            Item(nameof(options.RoiRight), FormatRatio(options.RoiRight), "분석 ROI의 오른쪽 경계 비율입니다."),
            Item(nameof(options.RoiBottom), FormatRatio(options.RoiBottom), "분석 ROI의 아래쪽 경계 비율입니다."),
            Item(nameof(options.MinPersonHeightRatio), FormatRatio(options.MinPersonHeightRatio), "사람으로 인정할 최소 화면 높이 비율입니다."),
            Item(nameof(options.PersonStableDurationSeconds), FormatSeconds(options.PersonStableDurationSeconds), "분석을 시작하기 전 사람이 안정적으로 머물러야 하는 시간입니다."),
            Item(nameof(options.PersonLeaveDurationSeconds), FormatSeconds(options.PersonLeaveDurationSeconds), "사람이 ROI를 벗어난 것으로 판단하기까지의 시간입니다.")
        ]),
        new SettingGroupPayload("분석 시간ㆍ프레임",
        [
            Item(nameof(options.MinAnalysisDurationSeconds), FormatSeconds(options.MinAnalysisDurationSeconds), "한 검사에 필요한 최소 분석 시간입니다."),
            Item(nameof(options.MaxAnalysisDurationSeconds), FormatSeconds(options.MaxAnalysisDurationSeconds), "한 검사에 허용되는 최대 분석 시간입니다."),
            Item(nameof(options.TargetAnalysisFrames), FormatFrames(options.TargetAnalysisFrames), "한 검사에서 수집하려는 목표 분석 프레임 수입니다."),
            Item(nameof(options.MinAnalysisFrames), FormatFrames(options.MinAnalysisFrames), "판정에 필요한 최소 분석 프레임 수입니다."),
            Item(nameof(options.MaxInferenceFps), $"{options.MaxInferenceFps.ToString(CultureInfo.InvariantCulture)} FPS", "초당 수행할 최대 AI 추론 횟수입니다.")
        ]),
        new SettingGroupPayload("판정",
        [
            Item(nameof(options.MinEvidenceFrames), FormatFrames(options.MinEvidenceFrames), "장비 상태 판정에 필요한 최소 증거 프레임 수입니다."),
            Item(nameof(options.MinEvidenceRatio), FormatRatio(options.MinEvidenceRatio), "증거로 인정할 분석 프레임의 최소 비율입니다."),
            Item(nameof(options.DecisionRatio), FormatRatio(options.DecisionRatio), "최종 착용 또는 미착용으로 결정하는 득표 비율입니다.")
        ]),
        new SettingGroupPayload("PPE 연결 영역",
        [
            Item(nameof(options.PersonHorizontalMarginRatio), FormatRatio(options.PersonHorizontalMarginRatio), "사람 영역 좌우에 적용하는 여유 비율입니다."),
            Item(nameof(options.HeadTopMarginRatio), FormatRatio(options.HeadTopMarginRatio), "머리 장비 영역 위쪽에 적용하는 여유 비율입니다."),
            Item(nameof(options.HardhatBottomRatio), FormatRatio(options.HardhatBottomRatio), "안전모 판정 영역의 아래쪽 경계 비율입니다."),
            Item(nameof(options.MaskBottomRatio), FormatRatio(options.MaskBottomRatio), "마스크 판정 영역의 아래쪽 경계 비율입니다."),
            Item(nameof(options.VestTopRatio), FormatRatio(options.VestTopRatio), "안전조끼 판정 영역의 위쪽 경계 비율입니다."),
            Item(nameof(options.VestBottomRatio), FormatRatio(options.VestBottomRatio), "안전조끼 판정 영역의 아래쪽 경계 비율입니다.")
        ]),
        new SettingGroupPayload("이미지ㆍ네트워크",
        [
            Item(nameof(options.CameraIndex), options.CameraIndex.ToString(CultureInfo.InvariantCulture), "서버가 사용하는 카메라 장치 인덱스입니다."),
            Item(nameof(options.CameraWidth), $"{options.CameraWidth.ToString(CultureInfo.InvariantCulture)} px", "카메라 입력 영상의 가로 해상도입니다."),
            Item(nameof(options.CameraHeight), $"{options.CameraHeight.ToString(CultureInfo.InvariantCulture)} px", "카메라 입력 영상의 세로 해상도입니다."),
            Item(nameof(options.JpegQuality), $"{options.JpegQuality.ToString(CultureInfo.InvariantCulture)} / 100", "저장 또는 전송 이미지의 JPEG 품질입니다."),
            Item(nameof(options.ListenPort), options.ListenPort.ToString(CultureInfo.InvariantCulture), "클라이언트 TCP 연결을 수신하는 서버 포트입니다.")
        ])
    ];

    private static SettingItemPayload Item(string key, string value, string description) => new(key, value, description);

    private static string FormatRatio(double value) =>
        $"{value.ToString("0.##", CultureInfo.InvariantCulture)} ({(value * 100).ToString("0.##", CultureInfo.InvariantCulture)}%)";

    private static string FormatSeconds(double value) =>
        $"{value.ToString("0.##", CultureInfo.InvariantCulture)}초";

    private static string FormatFrames(int value) =>
        $"{value.ToString(CultureInfo.InvariantCulture)} 프레임";

    private static string GetServerVersion()
    {
        try
        {
            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "알 수 없음";
        }
        catch (Exception)
        {
            return "알 수 없음";
        }
    }

    private static DateTimeOffset GetServerStartedAtUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    // 연결 문자열에서 공개 가능한 키만 읽고, 나머지 값은 문자열로 만들지 않는다.
    private static (string Host, string Name) ReadNonSecretDatabaseInfo(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return (string.Empty, string.Empty);

        try
        {
            string host = string.Empty;
            string name = string.Empty;
            if (!TryReadNonSecretDatabaseInfo(source.AsSpan(), ref host, ref name))
                return (string.Empty, string.Empty);

            return (host, name);
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static bool TryReadNonSecretDatabaseInfo(ReadOnlySpan<char> source, ref string host, ref string name)
    {
        int segmentStart = 0;
        char quote = '\0';
        for (int index = 0; index <= source.Length; index++)
        {
            if (index < source.Length && (source[index] == '\'' || source[index] == '"'))
            {
                if (quote == '\0') quote = source[index];
                else if (quote == source[index]) quote = '\0';
            }

            if (index != source.Length && (source[index] != ';' || quote != '\0')) continue;
            if (quote != '\0') return false;

            var segment = source[segmentStart..index].Trim();
            segmentStart = index + 1;
            if (segment.IsEmpty) continue;

            int separator = segment.IndexOf('=');
            if (separator <= 0) return false;

            var key = segment[..separator].Trim();
            var value = TrimOuterQuotes(segment[(separator + 1)..].Trim());
            if (key.Equals("Server".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || key.Equals("Host".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || key.Equals("Data Source".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                host = value.ToString();
            }
            else if (key.Equals("Database".AsSpan(), StringComparison.OrdinalIgnoreCase)
                     || key.Equals("Initial Catalog".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                name = value.ToString();
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> TrimOuterQuotes(ReadOnlySpan<char> value)
    {
        if (value.Length >= 2
            && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }
}
