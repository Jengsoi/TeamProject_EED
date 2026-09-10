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
    private int _reportedWidth;
    private int _reportedHeight;

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

    [ObservableProperty] private double roiLeft = 0.20;
    [ObservableProperty] private double roiTop = 0.05;
    [ObservableProperty] private double roiRight = 0.80;
    [ObservableProperty] private double roiBottom = 0.95;

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
    public event Func<bool>? ConfirmDiscardUnsavedRequested;

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
            try { await _connection.SendAsync(MessageTypes.InspectionSessionEnd, new InspectionSessionEndPayload("camera_error")); }
            catch (Exception) { /* 세션이 이미 없으면 무시 */ }
            return;
        }

        _capture.Set(VideoCaptureProperties.FrameWidth, ClientSettings.CameraWidth);
        _capture.Set(VideoCaptureProperties.FrameHeight, ClientSettings.CameraHeight);
        _reportedWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
        _reportedHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
        if (_reportedWidth <= 0) _reportedWidth = ClientSettings.CameraWidth;
        if (_reportedHeight <= 0) _reportedHeight = ClientSettings.CameraHeight;

        await StartSessionAsync();

        _captureRunning = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true };
        _captureThread.Start();
    }

    private async Task StartSessionAsync()
    {
        try
        {
            var envelope = await _connection.RequestAsync(MessageTypes.InspectionSessionStart, new InspectionSessionStartPayload(_reportedWidth, _reportedHeight));
            var started = envelope.DeserializePayload<InspectionSessionStartedPayload>();
            if (started.Success)
            {
                RoiLeft = started.RoiLeft;
                RoiTop = started.RoiTop;
                RoiRight = started.RoiRight;
                RoiBottom = started.RoiBottom;
                ConnectionMessage = null;
            }
            else
            {
                ConnectionMessage = started.ErrorMessage ?? "현장 검사 세션을 시작하지 못했습니다.";
            }
        }
        catch (Exception)
        {
            ConnectionMessage = "서버 연결이 끊겼습니다. 재연결을 시도합니다.";
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
                _ = SendFrameSafeAsync(meta, jpegBytes);
            }
        }
    }

    private async Task SendFrameSafeAsync(FrameMetaPayload meta, byte[] jpeg)
    {
        try { await _connection.SendFrameAsync(meta, jpeg); }
        catch (Exception) { /* 연결 문제는 Disconnected 이벤트로 처리한다 */ }
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
        _dispatcher.BeginInvoke(() => ConnectionMessage = "서버 연결이 끊겼습니다. 재연결을 시도합니다.");
        _ = TryReconnectAsync();
    }

    private async Task TryReconnectAsync()
    {
        bool ok = await _connection.ConnectWithRetryAsync(5, TimeSpan.FromSeconds(2));
        await _dispatcher.InvokeAsync(async () =>
        {
            if (ok)
            {
                ConnectionMessage = null;
                State = "WAITING"; // 재연결은 새 세션으로 간주하며 이전 상태를 복구하지 않는다.
                await StartSessionAsync();
            }
            else
            {
                ConnectionMessage = "서버에 연결할 수 없습니다. 연결을 확인해 주세요.";
            }
        });
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
        if (SaveState == SaveStatusCodes.SaveFailed)
        {
            bool proceed = ConfirmDiscardUnsavedRequested?.Invoke() ?? true;
            if (!proceed) return;
        }
        ReturnToDashboardRequested?.Invoke();
    }
}
