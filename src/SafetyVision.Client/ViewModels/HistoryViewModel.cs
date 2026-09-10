using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SafetyVision.Client.Networking;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.ViewModels;

// SCR-03 검사 이력.
public sealed partial class HistoryViewModel(ServerConnection connection) : ObservableObject
{
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int totalPages = 1;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private bool isDetailOpen;
    [ObservableProperty] private InspectionDetailResponsePayload? detail;
    [ObservableProperty] private BitmapImage? detailImage;

    public ObservableCollection<HistoryRowPayload> Rows { get; } = [];

    public bool CanGoPrevious => Page > 1;
    public bool CanGoNext => Page < TotalPages;

    public async Task LoadAsync(int targetPage)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var envelope = await connection.RequestAsync(MessageTypes.HistoryPageRequest, new HistoryPageRequestPayload(targetPage, null, null, null));
            var resp = envelope.DeserializePayload<HistoryPageResponsePayload>();
            Page = resp.Page;
            TotalPages = Math.Max(1, resp.TotalPages);
            TotalCount = resp.TotalCount;
            Rows.Clear();
            foreach (var r in resp.Rows) Rows.Add(r);
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
        }
        catch (Exception)
        {
            ErrorMessage = "검사 이력을 불러오지 못했습니다.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task PreviousPageAsync() => Page > 1 ? LoadAsync(Page - 1) : Task.CompletedTask;

    [RelayCommand]
    private Task NextPageAsync() => Page < TotalPages ? LoadAsync(Page + 1) : Task.CompletedTask;

    [RelayCommand]
    private async Task OpenDetailAsync(long id)
    {
        ErrorMessage = null;
        try
        {
            var envelope = await connection.RequestAsync(MessageTypes.InspectionDetailRequest, new InspectionDetailRequestPayload(id));
            var resp = envelope.DeserializePayload<InspectionDetailResponsePayload>();
            Detail = resp;
            DetailImage = null;

            if (resp.ImageAvailable && connection.TryTakeImage(envelope.CorrelationId, out var bytes) && bytes is not null)
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                DetailImage = bmp;
            }

            IsDetailOpen = true;
        }
        catch (Exception)
        {
            ErrorMessage = "상세 정보를 불러오지 못했습니다.";
        }
    }

    [RelayCommand]
    private void CloseDetail() => IsDetailOpen = false;
}
