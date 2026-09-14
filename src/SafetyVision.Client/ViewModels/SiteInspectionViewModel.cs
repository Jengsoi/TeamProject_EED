using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using SafetyVision.Client.Config;
using SafetyVision.Client.Models;
using SafetyVision.Client.Networking;
using SafetyVision.Protocol;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.ViewModels;

// SCR-04 현장 검사 + SCR-05 현장 검사 결과. 하나의 화면이 서버 상태에 따라 내용을 바꾼다.
public sealed partial class SiteInspectionViewModel : ObservableObject
{
    private readonly ServerConnection _connection;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clockTimer;

    private VideoCapture? _capture;
    private Thread? _captureThread;
    private volatile bool _captureRunning;
    private readonly object _frameSendGate = new();
    private PendingFrame? _latestFrame;
    private CancellationTokenSource? _frameSenderCts;
    private Task? _frameSenderTask;

    private sealed record PendingFrame(FrameMetaPayload Meta, byte[] Jpeg);

    public SiteInspectionViewModel(ServerConnection connection)
    {
        _connection = connection;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => CurrentTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    [ObservableProperty] private BitmapSource? cameraPreview;
    [ObservableProperty] private BitmapImage? resultImage;
    [ObservableProperty] private string state = "WAITING";
    [ObservableProperty] private string guidance = "검사 영역 안에 서 주세요.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetryInspection))]
    [NotifyPropertyChangedFor(nameof(CanRetrySave))]
    [NotifyPropertyChangedFor(nameof(CanReturnToDashboard))]
    [NotifyPropertyChangedFor(nameof(SaveStatusText))]
    private string saveState = "NONE";

    [ObservableProperty] private string? finalResultText;
    [ObservableProperty] private string? cameraErrorMessage;
    [ObservableProperty] private string? connectionMessage;
    [ObservableProperty] private string currentTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    [ObservableProperty] private string? currentInspectionKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoiBoxWidthRatio))]
    private double roiLeft = 0.20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoiBoxHeightRatio))]
    private double roiTop = 0.05;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoiBoxWidthRatio))]
    [NotifyPropertyChangedFor(nameof(RoiRightMarginRatio))]
    private double roiRight = 0.80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoiBoxHeightRatio))]
    [NotifyPropertyChangedFor(nameof(RoiBottomMarginRatio))]
    private double roiBottom = 0.95;

    // Grid Star 칸 너비/높이는 절대 비율이 아니라 "그 칸의 몫"이어야 하므로,
    // ROI 박스 칸은 RoiRight-RoiLeft(폭)/RoiBottom-RoiTop(높이)를, 마지막 칸은 나머지(1-RoiRight/1-RoiBottom)를 써야 한다.
    public double RoiBoxWidthRatio => RoiRight - RoiLeft;
    public double RoiRightMarginRatio => 1 - RoiRight;
    public double RoiBoxHeightRatio => RoiBottom - RoiTop;
    public double RoiBottomMarginRatio => 1 - RoiBottom;

    // 실제 카메라 해상도(비율). ROI 오버레이를 영상과 같은 고정 비율 캔버스에 겹쳐 항상 정렬되게 한다.
    [ObservableProperty] private int frameWidth = 1280;
    [ObservableProperty] private int frameHeight = 720;

    public ObservableCollection<EquipmentDisplayItem> ResultItems { get; } = [];

    public bool CanRetryInspection => SaveState == SaveStatusCodes.Saved;
    public bool CanRetrySave => SaveState == SaveStatusCodes.SaveFailed;
    public bool CanReturnToDashboard => SaveState != SaveStatusCodes.Saving;

    public string SaveStatusText => SaveState switch
    {
        SaveStatusCodes.Saving => "저장 중...",
        SaveStatusCodes.Saved => "저장 완료",
        SaveStatusCodes.SaveFailed => "결과를 저장하지 못했습니다.",
        _ => ""
    };

    public event Action? ReturnToDashboardRequested;
    public event Action? ConnectionLost;
    public event Func<bool>? ConfirmDiscardUnsavedRequested;

    public bool ConfirmLeave()
    {
        if (SaveState != SaveStatusCodes.SaveFailed) return true;
        return ConfirmDiscardUnsavedRequested?.Invoke() ?? true;
    }

    public async Task EnterAsync()
    {
        State = "WAITING";
        Guidance = "검사 영역 안에 서 주세요.";
        SaveState = "NONE";
        ResultImage = null;
        ResultItems.Clear();
        FinalResultText = null;
        CameraErrorMessage = null;
        ConnectionMessage = null;
        _clockTimer.Start();

        _connection.StateChanged += OnStateChanged;
        _connection.ResultReceived += OnResultReceived;
        _connection.SaveAckReceived += OnSaveAckReceived;
        _connection.ErrorReceived += OnErrorReceived;
        _connection.Disconnected += OnDisconnected;

        _capture = new VideoCapture(ClientSettings.CameraIndex);
        if (!_capture.IsOpened())
        {
            CameraErrorMessage = "카메라를 사용할 수 없습니다. 연결을 확인해 주세요.";
            await LeaveAsync();
            return;
        }

        _capture.Set(VideoCaptureProperties.FrameWidth, ClientSettings.CameraWidth);
        _capture.Set(VideoCaptureProperties.FrameHeight, ClientSettings.CameraHeight);
        int reportedWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
        int reportedHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
        FrameWidth = reportedWidth > 0 ? reportedWidth : ClientSettings.CameraWidth;
        FrameHeight = reportedHeight > 0 ? reportedHeight : ClientSettings.CameraHeight;

        if (!await StartSessionAsync())
        {
            await LeaveAsync();
            return;
        }

        _captureRunning = true;
        _frameSenderCts = new CancellationTokenSource();
        _captureThread = new Thread(CaptureLoop) { IsBackground = true };
        _captureThread.Start();
    }

    private async Task<bool> StartSessionAsync()
    {
        try
        {
            var envelope = await _connection.RequestAsync(MessageTypes.InspectionSessionStart, new InspectionSessionStartPayload(FrameWidth, FrameHeight, ClientSettings.CameraName));
            var started = envelope.DeserializePayload<InspectionSessionStartedPayload>();
            if (started.Success)
            {
                RoiLeft = started.RoiLeft;
                RoiTop = started.RoiTop;
                RoiRight = started.RoiRight;
                RoiBottom = started.RoiBottom;
                ConnectionMessage = null;
                return true;
            }
            else
            {
                ConnectionMessage = started.ErrorMessage ?? "현장 검사 세션을 시작하지 못했습니다.";
                return false;
            }
        }
        catch (Exception)
        {
            ConnectionMessage = "서버 연결이 끊겼습니다. 다시 로그인해 주세요.";
            return false;
        }
    }

    public async Task LeaveAsync()
    {
        _clockTimer.Stop();
        _captureRunning = false;
        _captureThread?.Join(500);
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;

        await StopFrameSenderAsync();

        _connection.StateChanged -= OnStateChanged;
        _connection.ResultReceived -= OnResultReceived;
        _connection.SaveAckReceived -= OnSaveAckReceived;
        _connection.ErrorReceived -= OnErrorReceived;
        _connection.Disconnected -= OnDisconnected;

        try { await _connection.SendAsync(MessageTypes.InspectionSessionEnd, new InspectionSessionEndPayload(null)); }
        catch (Exception) { /* 연결이 이미 끊겼으면 무시 */ }
    }

    private void CaptureLoop()
    {
        using var mat = new Mat();
        var sendInterval = TimeSpan.FromSeconds(1.0 / ClientSettings.MaxFrameSendFps);
        var lastSend = DateTime.MinValue;
        long seq = 0;

        while (_captureRunning)
        {
            if (_capture is null || !_capture.Read(mat) || mat.Empty())
            {
                Thread.Sleep(15);
                continue;
            }

            try
            {
                var preview = mat.ToBitmapSource();
                preview.Freeze();
                _dispatcher.BeginInvoke(() => CameraPreview = preview);
            }
            catch (Exception)
            {
                // 프레임 변환 실패는 다음 프레임에서 재시도한다.
            }

            var now = DateTime.UtcNow;
            if (now - lastSend >= sendInterval)
            {
                lastSend = now;
                seq++;
                Cv2.ImEncode(".jpg", mat, out var jpegBytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, ClientSettings.FrameJpegQuality));
                var meta = new FrameMetaPayload(seq, DateTimeOffset.UtcNow, mat.Width, mat.Height);
                QueueFrame(meta, jpegBytes);
            }
        }
    }

    private void QueueFrame(FrameMetaPayload meta, byte[] jpeg)
    {
        lock (_frameSendGate)
        {
            if (!_captureRunning || _frameSenderCts is null) return;
            _latestFrame = new PendingFrame(meta, jpeg);
            if (_frameSenderTask is null || _frameSenderTask.IsCompleted)
                _frameSenderTask = SendLatestFramesAsync(_frameSenderCts.Token);
        }
    }

    private async Task SendLatestFramesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PendingFrame? next;
                lock (_frameSendGate)
                {
                    if (!_captureRunning || _latestFrame is null) return;
                    next = _latestFrame;
                    _latestFrame = null;
                }

                try
                {
                    await _connection.SendFrameAsync(next.Meta, next.Jpeg, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task StopFrameSenderAsync()
    {
        Task? senderTask;
        CancellationTokenSource? senderCts;
        lock (_frameSendGate)
        {
            _latestFrame = null;
            senderCts = _frameSenderCts;
            _frameSenderCts = null;
            senderTask = _frameSenderTask;
            _frameSenderTask = null;
            senderCts?.Cancel();
        }

        if (senderTask is not null)
        {
            try { await senderTask.ConfigureAwait(false); }
            catch (Exception) { /* sender shutdown is best effort */ }
        }
        senderCts?.Dispose();
    }

    private void OnStateChanged(InspectionStateChangedPayload payload)
    {
        _dispatcher.BeginInvoke(() =>
        {
            State = payload.State;
            Guidance = payload.Guidance;
            SaveState = payload.SaveState;
            if (payload.State == "WAITING")
            {
                ResultImage = null;
                ResultItems.Clear();
                FinalResultText = null;
                CurrentInspectionKey = null;
            }
        });
    }

    private void OnResultReceived(InspectionResultPayload payload, byte[]? image)
    {
        _dispatcher.BeginInvoke(() =>
        {
            State = "RESULT";
            SaveState = payload.SaveState;
            FinalResultText = payload.Result;
            CurrentInspectionKey = payload.InspectionKey;

            ResultItems.Clear();
            foreach (var item in payload.Items)
                ResultItems.Add(new EquipmentDisplayItem(item.EquipmentCode, DashboardViewModel.LabelFor(item.EquipmentCode), item.Status, item.Score));

            if (image is not null)
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(image);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                ResultImage = bmp;
            }
        });
    }

    private void OnSaveAckReceived(SaveResultAckPayload payload)
    {
        _dispatcher.BeginInvoke(() => SaveState = payload.Status);
    }

    private void OnErrorReceived(ErrorNotificationPayload payload)
    {
        _dispatcher.BeginInvoke(() => Guidance = payload.Message);
    }

    private void OnDisconnected()
    {
        if (!_captureRunning) return;
        _captureRunning = false;
        _ = HandleDisconnectedAsync();
    }

    private async Task HandleDisconnectedAsync()
    {
        await _dispatcher.InvokeAsync(() => ConnectionMessage = "서버 연결이 끊겼습니다. 다시 로그인해 주세요.");
        await LeaveAsync();
        await _dispatcher.InvokeAsync(() => ConnectionLost?.Invoke());
    }

    [RelayCommand]
    private async Task RetryInspectionAsync()
    {
        if (!CanRetryInspection) return;
        try { await _connection.SendAsync(MessageTypes.RetryInspectionRequest, new RetryInspectionRequestPayload()); }
        catch (Exception) { /* Disconnected 이벤트가 별도 처리 */ }
    }

    [RelayCommand]
    private async Task RetrySaveAsync()
    {
        if (!CanRetrySave || CurrentInspectionKey is null) return;
        try { await _connection.SendAsync(MessageTypes.RetrySaveRequest, new RetrySaveRequestPayload(CurrentInspectionKey)); }
        catch (Exception) { /* Disconnected 이벤트가 별도 처리 */ }
    }

    [RelayCommand]
    private void ReturnToDashboard()
    {
        ReturnToDashboardRequested?.Invoke();
    }
}
