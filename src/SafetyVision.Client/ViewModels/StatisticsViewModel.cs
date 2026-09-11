using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Win32;
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

    [ObservableProperty] private ISeries[] dailySeries = [];
    [ObservableProperty] private Axis[] dailyXAxes = [];

    [ObservableProperty] private ISeries[] monthlySeries = [];
    [ObservableProperty] private Axis[] monthlyXAxes = [];

    [ObservableProperty] private ISeries[] cameraSeries = [];
    [ObservableProperty] private Axis[] cameraXAxes = [];

    public ObservableCollection<EquipmentBreakdownRow> Rows { get; } = [];
    public ObservableCollection<CameraBreakdownRow> CameraRows { get; } = [];

    private StatisticsResponsePayload? _lastStats;

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

    [RelayCommand]
    private void ExportCsv()
    {
        if (_lastStats is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "통계 내보내기",
            FileName = $"SafetyVision_통계_{DateTime.Now:yyyyMMdd_HHmm}.csv",
            Filter = "CSV 파일 (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildCsv(_lastStats), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ErrorMessage = null;
        }
        catch (Exception)
        {
            ErrorMessage = "파일로 내보내지 못했습니다.";
        }
    }

    private static string BuildCsv(StatisticsResponsePayload stats)
    {
        var sb = new StringBuilder();
        void Row(params object[] values) => sb.AppendLine(string.Join(",", values));

        Row("SafetyVision 통계", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        Row("전체 검사", stats.Total);
        Row("정상", stats.Normal);
        Row("점검 필요", stats.CheckRequired);
        sb.AppendLine();

        Row("[장비별 착용 현황]");
        Row("장비", "착용", "미착용", "미확인", "합계");
        foreach (var b in stats.Breakdown)
            Row(DashboardViewModel.LabelFor(b.EquipmentCode), b.Worn, b.NotWorn, b.Unknown, b.Worn + b.NotWorn + b.Unknown);
        sb.AppendLine();

        Row("[카메라별 현황]");
        Row("카메라", "전체", "정상", "점검 필요");
        foreach (var c in stats.CameraBreakdown)
            Row(c.CameraName, c.Total, c.Normal, c.CheckRequired);
        sb.AppendLine();

        Row("[일별 추이]");
        Row("날짜", "정상", "점검 필요");
        foreach (var d in stats.DailyTrend)
            Row(d.Date.ToString("yyyy-MM-dd"), d.Normal, d.CheckRequired);
        sb.AppendLine();

        Row("[월별 추이]");
        Row("연월", "정상", "점검 필요");
        foreach (var m in stats.MonthlyTrend)
            Row($"{m.Year}-{m.Month:00}", m.Normal, m.CheckRequired);

        return sb.ToString();
    }

    private void Apply(StatisticsResponsePayload stats)
    {
        _lastStats = stats;
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

        DailyXAxes = [new Axis { Labels = stats.DailyTrend.Select(d => d.Date.ToString("MM/dd")).ToArray() }];
        DailySeries =
        [
            new ColumnSeries<double> { Values = stats.DailyTrend.Select(d => (double)d.Normal).ToArray(), Name = "정상", Fill = ToSkia(StatusToBrushConverter.Worn) },
            new ColumnSeries<double> { Values = stats.DailyTrend.Select(d => (double)d.CheckRequired).ToArray(), Name = "점검 필요", Fill = ToSkia(StatusToBrushConverter.NotWorn) },
        ];

        MonthlyXAxes = [new Axis { Labels = stats.MonthlyTrend.Select(m => $"{m.Year}-{m.Month:00}").ToArray() }];
        MonthlySeries =
        [
            new ColumnSeries<double> { Values = stats.MonthlyTrend.Select(m => (double)m.Normal).ToArray(), Name = "정상", Fill = ToSkia(StatusToBrushConverter.Worn) },
            new ColumnSeries<double> { Values = stats.MonthlyTrend.Select(m => (double)m.CheckRequired).ToArray(), Name = "점검 필요", Fill = ToSkia(StatusToBrushConverter.NotWorn) },
        ];

        CameraRows.Clear();
        foreach (var c in stats.CameraBreakdown)
            CameraRows.Add(new CameraBreakdownRow(c.CameraName, c.Total, c.Normal, c.CheckRequired));

        CameraXAxes = [new Axis { Labels = stats.CameraBreakdown.Select(c => c.CameraName).ToArray() }];
        CameraSeries =
        [
            new ColumnSeries<double> { Values = stats.CameraBreakdown.Select(c => (double)c.Normal).ToArray(), Name = "정상", Fill = ToSkia(StatusToBrushConverter.Worn) },
            new ColumnSeries<double> { Values = stats.CameraBreakdown.Select(c => (double)c.CheckRequired).ToArray(), Name = "점검 필요", Fill = ToSkia(StatusToBrushConverter.NotWorn) },
        ];
    }

    private static SolidColorPaint ToSkia(System.Windows.Media.SolidColorBrush brush) =>
        new(new SKColor(brush.Color.R, brush.Color.G, brush.Color.B));
}

public sealed record EquipmentBreakdownRow(string Label, int Worn, int NotWorn, int Unknown)
{
    public int Total => Worn + NotWorn + Unknown;
}

public sealed record CameraBreakdownRow(string CameraName, int Total, int Normal, int CheckRequired);
