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
        // 임시 조치(팀 결정): 최종 결과는 정상/점검 필요 2가지로 단순화. 예전 데이터의 미확인(Unconfirmed)도
        // 화면에서는 점검 필요로 합산해서 보여준다.
        Total = stats.Total;
        Normal = stats.Normal;
        CheckRequired = stats.CheckRequired + stats.Unconfirmed;
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
            new PieSeries<double> { Values = [CheckRequired], Name = "점검 필요", Fill = StatusToBrushToSkia(StatusToBrushConverter.NotWorn) },
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
