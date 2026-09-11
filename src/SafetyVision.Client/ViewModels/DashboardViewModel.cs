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

// SCR-02 관리자 대시보드.
public sealed partial class DashboardViewModel(ServerConnection connection) : ObservableObject
{
    [ObservableProperty] private int total;
    [ObservableProperty] private int normal;
    [ObservableProperty] private int checkRequired;
    [ObservableProperty] private int unconfirmed;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private ISeries[] barSeries = [];
    [ObservableProperty] private Axis[] barXAxes = [new Axis { Labels = ["안전모", "안전조끼", "마스크"] }];
    [ObservableProperty] private ISeries[] pieSeries = [];

    public ObservableCollection<RecentInspectionPayload> Recent { get; } = [];

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var envelope = await connection.RequestAsync(MessageTypes.DashboardStatsRequest, new DashboardStatsRequestPayload());
            var stats = envelope.DeserializePayload<DashboardStatsResponsePayload>();
            Apply(stats);
        }
        catch (Exception)
        {
            ErrorMessage = "대시보드 정보를 불러오지 못했습니다.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(DashboardStatsResponsePayload stats)
    {
        Total = stats.Total;
        Normal = stats.Normal;
        CheckRequired = stats.CheckRequired;
        Unconfirmed = stats.Unconfirmed;
        IsEmpty = stats.Total == 0;

        Recent.Clear();
        foreach (var r in stats.Recent) Recent.Add(r);

        var order = new[] { "hardhat", "vest", "mask" };
        var values = order
            .Select(code => stats.EquipmentRates.FirstOrDefault(r => r.EquipmentCode == code)?.WornRatio ?? 0)
            .Select(r => Math.Round(r * 100, 0))
            .ToArray();

        BarSeries =
        [
            new ColumnSeries<double>
            {
                Values = values,
                Fill = new SolidColorPaint(new SKColor(0x25, 0x63, 0xEB)),
                Name = "착용률",
                DataLabelsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x33)),
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.Top,
                DataLabelsFormatter = point => $"{point.Coordinate.PrimaryValue:0}%",
            }
        ];

        PieSeries =
        [
            new PieSeries<double> { Values = [stats.Normal], Name = "정상", Fill = StatusToBrushToSkia(StatusToBrushConverter.Worn) },
            new PieSeries<double> { Values = [stats.CheckRequired], Name = "점검 필요", Fill = StatusToBrushToSkia(StatusToBrushConverter.NotWorn) },
            new PieSeries<double> { Values = [stats.Unconfirmed], Name = "미착용", Fill = new SolidColorPaint(new SKColor(0xDC, 0x26, 0x26)) },
        ];
    }

    private static SolidColorPaint StatusToBrushToSkia(System.Windows.Media.SolidColorBrush brush) =>
        new(new SKColor(brush.Color.R, brush.Color.G, brush.Color.B));

    public static string LabelFor(string code) => code switch
    {
        "hardhat" => "안전모",
        "vest" => "안전조끼",
        "mask" => "마스크",
        _ => code
    };
}
