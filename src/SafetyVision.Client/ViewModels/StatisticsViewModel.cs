using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SafetyVision.Client.Converters;
using SafetyVision.Client.Networking;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;
using SkiaSharp;

namespace SafetyVision.Client.ViewModels;

// 임시 조치(팀 결정): 원래 문서 스펙은 "통계 분석" 메뉴를 버튼만(준비 중인 기능입니다) 두었으나,
// 실제 화면을 만들기로 함. 대시보드보다 상세한 장비별 착용/미착용/미확인 건수를 보여준다.
public sealed partial class StatisticsViewModel(ServerConnection connection) : ObservableObject
{
    [ObservableProperty] private int total;
    [ObservableProperty] private int normal;
    [ObservableProperty] private int checkRequired;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private ISeries[] breakdownSeries = [];
    [ObservableProperty] private Axis[] breakdownXAxes = [new Axis { Labels = ["안전모", "안전조끼", "마스크"] }];

    public ObservableCollection<EquipmentBreakdownRow> Rows { get; } = [];

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var envelope = await connection.RequestAsync(MessageTypes.StatisticsRequest, new StatisticsRequestPayload());
            var stats = envelope.DeserializePayload<StatisticsResponsePayload>();
            Apply(stats);
        }
        catch (Exception)
        {
            ErrorMessage = "통계 정보를 불러오지 못했습니다.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(StatisticsResponsePayload stats)
    {
        Total = stats.Total;
        Normal = stats.Normal;
        CheckRequired = stats.CheckRequired;
        IsEmpty = stats.Total == 0;

        var order = new[] { "hardhat", "vest", "mask" };
        var byCode = stats.Breakdown.ToDictionary(b => b.EquipmentCode);

        Rows.Clear();
        foreach (var code in order)
        {
            var b = byCode.GetValueOrDefault(code) ?? new EquipmentBreakdownPayload(code, 0, 0, 0);
            Rows.Add(new EquipmentBreakdownRow(DashboardViewModel.LabelFor(code), b.Worn, b.NotWorn, b.Unknown));
        }

        double[] worn = order.Select(c => (double)(byCode.GetValueOrDefault(c)?.Worn ?? 0)).ToArray();
        double[] notWorn = order.Select(c => (double)(byCode.GetValueOrDefault(c)?.NotWorn ?? 0)).ToArray();
        double[] unknown = order.Select(c => (double)(byCode.GetValueOrDefault(c)?.Unknown ?? 0)).ToArray();

        BreakdownSeries =
        [
            new ColumnSeries<double> { Values = worn, Name = "착용", Fill = ToSkia(StatusToBrushConverter.Worn) },
            new ColumnSeries<double> { Values = notWorn, Name = "미착용", Fill = ToSkia(StatusToBrushConverter.NotWorn) },
            new ColumnSeries<double> { Values = unknown, Name = "미확인", Fill = ToSkia(StatusToBrushConverter.Unknown) },
        ];
    }

    private static SolidColorPaint ToSkia(System.Windows.Media.SolidColorBrush brush) =>
        new(new SKColor(brush.Color.R, brush.Color.G, brush.Color.B));
}

public sealed record EquipmentBreakdownRow(string Label, int Worn, int NotWorn, int Unknown)
{
    public int Total => Worn + NotWorn + Unknown;
}
