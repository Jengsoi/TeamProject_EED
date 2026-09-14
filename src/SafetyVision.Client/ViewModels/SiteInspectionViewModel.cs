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
    private readonly SemaphoreSlim _frameSendGate = new(1, 1);
    private int _cameraFailureReported;
    private int _reconnectRunning;
    private bool _isEntered;

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

    // ROI 오버레이의 가운데 컬럼/행(박스)과 마지막 컬럼/행(오른쪽/아래쪽 여백)에 바인딩되는 값.
    // RoiRight/RoiBottom을 별(star) 가중치로 직접 쓰면 안 되고, 반드시 구간 폭(차이값)으로 변환해야 한다.
    [ObservableProperty] private double roiBoxWidth = 0.60;
    [ObservableProperty] private double roiRightMargin = 0.20;
    [ObservableProperty] private double roiBoxHeight = 0.90;
    [ObservableProperty] private double roiBottomMargin = 0.05;

    // 미리보기 Viewbox 내부 Grid의 크기. 카메라의 실제 종횡비와 동일하게 유지해
    // Stretch로 인한 레터박스와 무관하게 ROI 오버레이가 항상 실제 영상 영역과 일치하도록 한다.
    [ObservableProperty] private double frameWidth = ClientSettings.CameraWidth;
    [ObservableProperty] private double frameHeight = ClientSettings.CameraHeight;

    partial void OnRoiLeftChanged(double value) => UpdateRoiSpans();
    partial void OnRoiTopChanged(double value) => UpdateRoiSpans();
    partial void OnRoiRightChanged(double value) => UpdateRoiSpans();
    partial void OnRoiBottomChanged(double value) => UpdateRoiSpans();

    private void UpdateRoiSpans()
    {
        RoiBoxWidth = Math.Max(0.0001, RoiRight - RoiLeft);
        RoiRightMargin = Math.Max(0.0001, 1 - RoiRight);
        RoiBoxHeight = Math.Max(0.0001, RoiBottom - RoiTop);
        RoiBottomMargin = Math.Max(0.0001, 1 - RoiBottom);
    }

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
    public event Action? ReauthenticationRequired;

    public async Task EnterAsync()
    {
        if (_isEntered) return;
        _isEntered = true;
        Interlocked.Exchange(ref _cameraFailureReported, 0);
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
            _capture.Dispose();
            _capture = null;
            UnsubscribeConnectionEvents();
            _clockTimer.Stop();
            _isEntered = false;
            return;
        }

        _capture.Set(VideoCaptureProperties.FrameWidth, ClientSettings.CameraWidth);
        _capture.Set(VideoCaptureProperties.FrameHeight, ClientSettings.CameraHeight);
        _reportedWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
        _reportedHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
        if (_reportedWidth <= 0) _reportedWidth = ClientSettings.CameraWidth;
        if (_reportedHeight <= 0) _reportedHeight = ClientSettings.CameraHeight;

        // 미리보기 Viewbox가 실제 카메라 종횡비를 그대로 따라가도록 갱신 (ROI 오버레이 정합성 유지).
        FrameWidth = _reportedWidth;
        FrameHeight = _reportedHeight;

        await StartSessionAsync();

        _captureRunning = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true };
        _captureThread.Start();
    }

    private async Task StartSessionAsync()
    {
        try
        {
            var envelope = await _connection.RequestAsync(MessageTypes.InspectionSessionStart,
                new InspectionSessionStartPayload(_reportedWidth, _reportedHeight, "CAM 01"));
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
        if (!_isEntered) return;
        _isEntered = false;
        _clockTimer.Stop();
        _captureRunning = false;
        var capture = _capture;
        var captureThread = _captureThread;
        _capture = null;
        bool captureStopped = captureThread?.Join(2000) ?? true;
        if (captureStopped)
        {
            capture?.Release();
            capture?.Dispose();
        }
        else if (captureThread is not null && capture is not null)
        {
            _ = Task.Run(() =>
            {
                captureThread.Join();
                capture.Release();
                capture.Dispose();
            });
        }
        _captureThread = null;

        UnsubscribeConnectionEvents();

        try { await _connection.SendAsync(MessageTypes.InspectionSessionEnd, new InspectionSessionEndPayload(null)); }
        catch (Exception) { /* 연결이 이미 끊겼으면 무시 */ }
    }

    private void CaptureLoop()
    {
        var capture = _capture;
        if (capture is null) return;
        using var mat = new Mat();
        var sendInterval = TimeSpan.FromSeconds(1.0 / ClientSettings.MaxFrameSendFps);
        var lastSend = DateTime.MinValue;
        long seq = 0;
        int consecutiveReadFailures = 0;

        while (_captureRunning)
        {
            if (!capture.Read(mat) || mat.Empty())
            {
                Thread.Sleep(15);
                if (++consecutiveReadFailures >= 100)
                {
                    _captureRunning = false;
                    _ = ReportCameraFailureAsync();
                }
                continue;
            }
            consecutiveReadFailures = 0;

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
        if (!await _frameSendGate.WaitAsync(0)) return;
        try { await _connection.SendFrameAsync(meta, jpeg); }
        catch (Exception) { /* 연결 문제는 Disconnected 이벤트로 처리한다 */ }
        finally { _frameSendGate.Release(); }
    }

    private async Task ReportCameraFailureAsync()
    {
        if (Interlocked.Exchange(ref _cameraFailureReported, 1) != 0) return;
        await _dispatcher.InvokeAsync(() =>
        {
            CameraErrorMessage = "카메라 연결이 끊겼습니다. 장치를 확인한 뒤 검사 화면에 다시 진입해 주세요.";
            Guidance = "검사가 중단되었습니다.";
        });
        try { await _connection.SendAsync(MessageTypes.InspectionSessionEnd, new InspectionSessionEndPayload("camera_disconnected")); }
        catch (Exception) { /* 연결 종료와 동시에 발생할 수 있음 */ }
    }

    private void UnsubscribeConnectionEvents()
    {
        _connection.StateChanged -= OnStateChanged;
        _connection.ResultReceived -= OnResultReceived;
        _connection.SaveAckReceived -= OnSaveAckReceived;
        _connection.ErrorReceived -= OnErrorReceived;
        _connection.Disconnected -= OnDisconnected;
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
        if (Interlocked.CompareExchange(ref _reconnectRunning, 1, 0) == 0)
            _ = TryReconnectAsync();
    }

    private async Task TryReconnectAsync()
    {
        try
        {
            bool ok = await _connection.ConnectWithRetryAsync(5, TimeSpan.FromSeconds(2));
            await _dispatcher.InvokeAsync(async () =>
            {
                if (ok && _isEntered)
                {
                    // 서버의 새 TCP 세션은 인증되지 않은 상태다. 이전 로그인 권한을 암묵적으로
                    // 승계하지 않고 로그인 화면으로 돌아가 새 세션을 인증한다.
                    ConnectionMessage = "서버에 다시 연결했습니다. 다시 로그인해 주세요.";
                    await LeaveAsync();
                    ReauthenticationRequired?.Invoke();
                }
                else if (!ok && _isEntered)
                {
                    ConnectionMessage = "서버에 연결할 수 없습니다. 연결을 확인해 주세요.";
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectRunning, 0);
        }
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
