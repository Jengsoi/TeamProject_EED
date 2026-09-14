using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using SafetyVision.Client.Config;
using SafetyVision.Client.Services;

namespace SafetyVision.Client.ViewModels;

/// <summary>
/// 카메라 관리 화면에서 선택할 수 있는 해상도입니다.
/// </summary>
public sealed record CameraResolution(int Width, int Height)
{
    public string DisplayName => $"{Width} x {Height}";
}

/// <summary>
/// 카메라 탐색, 미리보기 및 사용자 설정 저장을 담당합니다.
/// </summary>
public sealed partial class CameraManagementViewModel : ObservableObject
{
    private const string NoCameraMessage = "카메라를 찾을 수 없습니다. 연결을 확인해 주세요.";

    private readonly object _previewSync = new();
    private readonly object _enumerationSync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private CancellationTokenSource? _previewCancellation;
    private Task? _previewTask;
    private CancellationTokenSource? _enumerationCancellation;

    public ObservableCollection<CameraProbe.CameraDeviceInfo> Cameras { get; } = [];

    public ObservableCollection<CameraResolution> ResolutionPresets { get; } =
    [
        new CameraResolution(1280, 720),
        new CameraResolution(1920, 1080),
        new CameraResolution(640, 480)
    ];

    [ObservableProperty] private CameraProbe.CameraDeviceInfo? selectedCamera;
    [ObservableProperty] private CameraResolution? selectedResolution;
    [ObservableProperty] private BitmapSource? cameraPreview;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private bool isPreviewRunning;

    public CameraManagementViewModel()
    {
        var options = CameraOptionsStore.Current;
        SelectedResolution = ResolutionPresets.FirstOrDefault(p =>
            p.Width == options.CameraWidth && p.Height == options.CameraHeight)
            ?? ResolutionPresets[0];
    }

    public async Task LoadAsync()
    {
        await RefreshCamerasAsync();
    }

    /// <summary>
    /// 화면을 떠날 때 호출한다. 취소만 하고 해제하는 방식이 아니라, 읽기 루프의 종료를 확인한다.
    /// </summary>
    public void Stop()
    {
        CancelEnumeration();
        StopPreview();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await RefreshCamerasAsync();
    }

    [RelayCommand]
    private async Task StartPreviewAsync()
    {
        StopPreview();

        var camera = SelectedCamera;
        var resolution = SelectedResolution;
        if (camera is null || resolution is null) return;

        await InvokeOnUiAsync(() =>
        {
            CameraPreview = null;
            IsPreviewRunning = false;
            StatusText = "미리보기를 시작하는 중입니다.";
        });

        var cancellation = new CancellationTokenSource();
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Task.Factory.StartNew(
            () =>
            {
                startGate.Task.GetAwaiter().GetResult();
                PreviewSessionLoop(camera.Index, resolution.Width, resolution.Height, cancellation);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        lock (_previewSync)
        {
            _previewCancellation = cancellation;
            _previewTask = session;
        }

        startGate.TrySetResult(true);
        _ = ObservePreviewCompletionAsync(session, cancellation);
    }

    [RelayCommand]
    private Task StopPreviewAsync()
    {
        StopPreview();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Save()
    {
        var options = CameraOptionsStore.Current;
        if (SelectedCamera is not null)
            options.CameraIndex = SelectedCamera.Index;

        if (SelectedResolution is not null)
        {
            options.CameraWidth = SelectedResolution.Width;
            options.CameraHeight = SelectedResolution.Height;
        }

        CameraOptionsStore.Save();
        InvokeOnUiAsync(() => StatusText = "카메라 설정을 저장했습니다.").GetAwaiter().GetResult();
    }

    partial void OnSelectedCameraChanging(CameraProbe.CameraDeviceInfo? value)
    {
        if (!Equals(SelectedCamera, value))
            StopPreview();
    }

    partial void OnSelectedResolutionChanging(CameraResolution? value)
    {
        if (!Equals(SelectedResolution, value))
            StopPreview();
    }

    private async Task RefreshCamerasAsync()
    {
        var cancellation = new CancellationTokenSource();
        lock (_enumerationSync)
        {
            try { _enumerationCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }

            _enumerationCancellation = cancellation;
        }

        await _refreshGate.WaitAsync();
        try
        {
            if (cancellation.IsCancellationRequested) return;

            // 탐색 중에는 미리보기 장치를 닫아 같은 장치에 대한 경합을 없앤다.
            StopPreview();
            await InvokeOnUiAsync(() =>
            {
                if (IsCurrentEnumeration(cancellation))
                    StatusText = "카메라를 검색하는 중입니다.";
            });

            var devices = await CameraProbe.EnumerateAsync(7, cancellation.Token).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested) return;

            await InvokeOnUiAsync(() =>
            {
                if (!IsCurrentEnumeration(cancellation)) return;

                Cameras.Clear();
                foreach (var device in devices)
                    Cameras.Add(device);

                if (Cameras.Count == 0)
                {
                    SelectedCamera = null;
                    StatusText = NoCameraMessage;
                    return;
                }

                var configuredIndex = CameraOptionsStore.Current.CameraIndex;
                SelectedCamera = Cameras.FirstOrDefault(device => device.Index == configuredIndex) ?? Cameras[0];
                StatusText = $"{Cameras.Count}대의 카메라를 찾았습니다. 장치를 선택하고 미리보기를 시작하세요.";
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 새로고침 또는 화면 이탈로 취소된 탐색은 별도 오류로 표시하지 않는다.
        }
        catch (Exception)
        {
            await InvokeOnUiAsync(() =>
            {
                if (IsCurrentEnumeration(cancellation))
                    StatusText = NoCameraMessage;
            });
        }
        finally
        {
            lock (_enumerationSync)
            {
                if (ReferenceEquals(_enumerationCancellation, cancellation))
                    _enumerationCancellation = null;
            }

            cancellation.Dispose();
            _refreshGate.Release();
        }
    }

    private void PreviewSessionLoop(int cameraIndex, int width, int height, CancellationTokenSource owner)
    {
        VideoCapture? capture = null;
        try
        {
            if (owner.IsCancellationRequested) return;

            capture = new VideoCapture(cameraIndex);
            if (!capture.IsOpened())
            {
                PostPreviewUpdate(owner, () => StatusText = "선택한 카메라를 열 수 없습니다. 다른 장치를 선택해 주세요.");
                return;
            }

            if (owner.IsCancellationRequested) return;

            capture.Set(VideoCaptureProperties.FrameWidth, width);
            capture.Set(VideoCaptureProperties.FrameHeight, height);
            PostPreviewUpdate(owner, () =>
            {
                IsPreviewRunning = true;
                StatusText = "미리보기를 표시하고 있습니다.";
            });

            using var frame = new Mat();
            while (!owner.IsCancellationRequested)
            {
                bool readSucceeded;
                try
                {
                    readSucceeded = capture.Read(frame);
                }
                catch (Exception)
                {
                    if (!owner.IsCancellationRequested)
                        PostPreviewUpdate(owner, () => StatusText = "카메라 프레임을 읽을 수 없습니다.");
                    break;
                }

                if (!readSucceeded || frame.Empty())
                {
                    owner.Token.WaitHandle.WaitOne(15);
                    continue;
                }

                try
                {
                    var preview = frame.ToBitmapSource();
                    preview.Freeze();
                    PostPreviewUpdate(owner, () => CameraPreview = preview);
                }
                catch (Exception)
                {
                    // 일시적인 프레임 변환 오류는 다음 프레임에서 다시 시도한다.
                }
            }
        }
        catch (Exception)
        {
            if (!owner.IsCancellationRequested)
                PostPreviewUpdate(owner, () => StatusText = "카메라 미리보기를 시작할 수 없습니다.");
        }
        finally
        {
            // Read 호출이 끝나 루프를 빠져나온 뒤에만 장치를 해제한다.
            if (capture is not null)
            {
                try { capture.Release(); }
                catch (Exception) { }

                try { capture.Dispose(); }
                catch (Exception) { }
            }
        }
    }

    private async Task ObservePreviewCompletionAsync(Task session, CancellationTokenSource cancellation)
    {
        try
        {
            await session.ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested)
                PostPreviewUpdate(cancellation, () => StatusText = "카메라 미리보기가 예기치 않게 종료되었습니다.");
        }

        var ownsSession = false;
        lock (_previewSync)
        {
            if (ReferenceEquals(_previewTask, session) && ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewTask = null;
                _previewCancellation = null;
                ownsSession = true;
            }
        }

        if (!ownsSession) return;

        cancellation.Dispose();
        await InvokeOnUiAsync(() =>
        {
            lock (_previewSync)
            {
                if (_previewTask is not null) return;
            }

            IsPreviewRunning = false;
            CameraPreview = null;
        });
    }

    private void StopPreview()
    {
        Task? session;
        CancellationTokenSource? cancellation;
        lock (_previewSync)
        {
            session = _previewTask;
            cancellation = _previewCancellation;
        }

        if (session is null || cancellation is null) return;

        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        // 시간 제한 없는 대기로 Read 루프의 실제 종료를 보장한다.
        try { session.GetAwaiter().GetResult(); }
        catch (Exception) { }

        var ownsSession = false;
        lock (_previewSync)
        {
            if (ReferenceEquals(_previewTask, session) && ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewTask = null;
                _previewCancellation = null;
                ownsSession = true;
            }
        }

        if (ownsSession)
            cancellation.Dispose();

        InvokeOnUiAsync(() =>
        {
            lock (_previewSync)
            {
                if (_previewTask is not null) return;
            }

            IsPreviewRunning = false;
            CameraPreview = null;
            StatusText = "미리보기를 중지했습니다.";
        }).GetAwaiter().GetResult();
    }

    private void CancelEnumeration()
    {
        lock (_enumerationSync)
        {
            try { _enumerationCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private bool IsCurrentEnumeration(CancellationTokenSource cancellation)
    {
        lock (_enumerationSync)
            return ReferenceEquals(_enumerationCancellation, cancellation);
    }

    private void PostPreviewUpdate(CancellationTokenSource owner, Action update)
    {
        _ = InvokeOnUiAsync(() =>
        {
            lock (_previewSync)
            {
                if (!ReferenceEquals(_previewCancellation, owner)) return;
            }

            update();
        });
    }

    private static async Task InvokeOnUiAsync(Action action)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            await dispatcher.InvokeAsync(action).Task;
        }
        catch (TaskCanceledException)
        {
            // 앱 종료로 Dispatcher 작업이 취소된 경우에는 화면 갱신이 더 이상 필요 없다.
        }
        catch (InvalidOperationException)
        {
            // Dispatcher가 종료 중이면 Stop()도 예외 없이 끝나야 한다.
        }
    }
}
