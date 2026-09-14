using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafetyVision.Client.Networking;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.ViewModels;

public sealed record EquipmentManagementItem(
    string EquipmentCode,
    string DisplayName,
    int Worn,
    int NotWorn,
    int Unknown,
    int Total,
    string WornRatioText,
    string AverageScoreText,
    string PositiveModelClassesText,
    string NegativeModelClassesText,
    string BodyRegionText,
    string HorizontalMarginText);

public sealed record JudgingThresholdItem(string Name, string Value, string Description);

// SCR-07 장비 관리: 서버 설정과 실제 누적 판정 결과를 읽기 전용으로 표시한다.
public sealed partial class EquipmentManagementViewModel(ServerConnection connection) : ObservableObject
{
    private static readonly string[] EquipmentCodes = ["hardhat", "vest", "mask"];

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string modelName = "-";
    [ObservableProperty] private string modelVersion = "-";

    public ObservableCollection<EquipmentManagementItem> Equipment { get; } = [];
    public ObservableCollection<JudgingThresholdItem> JudgingThresholds { get; } = [];

    public async Task LoadAsync()
    {
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                IsBusy = true;
                ErrorMessage = null;
            });

            var envelope = await connection.RequestAsync(
                MessageTypes.EquipmentManagementRequest,
                new EquipmentManagementRequestPayload(),
                TimeSpan.FromSeconds(10));
            var response = envelope.DeserializePayload<EquipmentManagementResponsePayload>();

            await RunOnUiThreadAsync(() => Apply(response));
        }
        catch (Exception)
        {
            try
            {
                await RunOnUiThreadAsync(() =>
                    ErrorMessage = "장비 관리 정보를 불러오지 못했습니다. 잠시 후 다시 시도해 주세요.");
            }
            catch (Exception)
            {
                // 애플리케이션 종료 중에는 UI 갱신 예외를 외부로 전파하지 않는다.
            }
        }
        finally
        {
            try
            {
                await RunOnUiThreadAsync(() => IsBusy = false);
            }
            catch (Exception)
            {
                // 애플리케이션 종료 중에는 UI 갱신 예외를 외부로 전파하지 않는다.
            }
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private void Apply(EquipmentManagementResponsePayload response)
    {
        ModelName = response.ModelName;
        ModelVersion = response.ModelVersion;

        var rulesByCode = response.Rules.ToDictionary(rule => rule.EquipmentCode, StringComparer.Ordinal);
        var usageByCode = response.Usage.ToDictionary(usage => usage.EquipmentCode, StringComparer.Ordinal);

        Equipment.Clear();
        foreach (var code in EquipmentCodes)
        {
            if (!rulesByCode.TryGetValue(code, out var rule)) continue;
            usageByCode.TryGetValue(code, out var usage);

            int worn = usage?.Worn ?? 0;
            int notWorn = usage?.NotWorn ?? 0;
            int unknown = usage?.Unknown ?? 0;
            int total = usage?.Total ?? 0;
            Equipment.Add(new EquipmentManagementItem(
                code,
                rule.DisplayName,
                worn,
                notWorn,
                unknown,
                total,
                ToPercentText(usage?.WornRatio),
                ToPercentText(usage?.AverageScore),
                string.Join(", ", rule.PositiveModelClasses),
                string.Join(", ", rule.NegativeModelClasses),
                $"{BodyRegionName(code)}: {ToPercentRange(rule.RegionTopRatio, rule.RegionBottomRatio)} (사람 상단 기준)",
                $"좌우 여유: ±{rule.HorizontalMarginRatio * 100:0.#}% (사람 폭 기준)"));
        }

        JudgingThresholds.Clear();
        JudgingThresholds.Add(new JudgingThresholdItem(
            "검출 신뢰도", ToPercentText(response.DetectionConfidence), "이 값 이상의 탐지 박스를 판정에 사용합니다."));
        JudgingThresholds.Add(new JudgingThresholdItem(
            "NMS IoU 임계값", ToPercentText(response.NmsIouThreshold), "겹치는 탐지 박스를 하나로 정리하는 기준입니다."));
        JudgingThresholds.Add(new JudgingThresholdItem(
            "최소 분석 프레임", $"{response.MinAnalysisFrames} 프레임", "이보다 적게 분석되면 미확인으로 판정합니다."));
        JudgingThresholds.Add(new JudgingThresholdItem(
            "목표 분석 프레임", $"{response.TargetAnalysisFrames} 프레임", "한 사람을 분석할 때 수집을 목표로 하는 프레임 수입니다."));
        JudgingThresholds.Add(new JudgingThresholdItem(
            "최소 증거 프레임", $"{response.MinEvidenceFrames} 프레임", "착용 또는 미착용 증거가 최소 이 수 이상이어야 합니다."));
        JudgingThresholds.Add(new JudgingThresholdItem(
            "최소 증거 비율", ToPercentText(response.MinEvidenceRatio), "분석 프레임 수에 비례해 필요한 증거 수를 계산하는 기준입니다."));
        JudgingThresholds.Add(new JudgingThresholdItem(
            "판정 비율", ToPercentText(response.DecisionRatio), "증거 중 착용 또는 미착용 비율이 이 값 이상일 때 해당 상태로 판정합니다."));
    }

    private static string BodyRegionName(string code) => code == "vest" ? "몸통 영역" : "머리 영역";

    private static string ToPercentRange(double topRatio, double bottomRatio) =>
        $"{topRatio * 100:0.#}% ~ {bottomRatio * 100:0.#}%";

    private static string ToPercentText(double? ratio) => ratio is { } value ? $"{value * 100:0.#}%" : "-";

    private static async Task RunOnUiThreadAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action);
    }
}
