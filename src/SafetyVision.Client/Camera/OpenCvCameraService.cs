using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using SafetyVision.Client.Config;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.Camera;

public sealed class OpenCvCameraService : ICameraService
{
    private VideoCapture? _capture;
    private Thread? _captureThread;
    private volatile bool _running;

    public int Width { get; private set; } = ClientSettings.CameraWidth;
    public int Height { get; private set; } = ClientSettings.CameraHeight;
    public event Action<BitmapSource>? PreviewReady;
    public event Action<FrameMetaPayload, byte[]>? FrameReady;
    public event Action? ConnectionLost;

    public bool Start()
    {
        if (_running) return true;
        var capture = new VideoCapture(ClientSettings.CameraIndex);
        if (!capture.IsOpened())
        {
            capture.Dispose();
            return false;
        }
        capture.Set(VideoCaptureProperties.FrameWidth, ClientSettings.CameraWidth);
        capture.Set(VideoCaptureProperties.FrameHeight, ClientSettings.CameraHeight);
        Width = PositiveOrDefault((int)capture.Get(VideoCaptureProperties.FrameWidth), ClientSettings.CameraWidth);
        Height = PositiveOrDefault((int)capture.Get(VideoCaptureProperties.FrameHeight), ClientSettings.CameraHeight);
        _capture = capture;
        _running = true;
        _captureThread = new Thread(CaptureLoop) { IsBackground = true };
        _captureThread.Start();
        return true;
    }

    private void CaptureLoop()
    {
        var capture = _capture;
        if (capture is null) return;
        using var frame = new Mat();
        var sendInterval = TimeSpan.FromSeconds(1.0 / ClientSettings.MaxFrameSendFps);
        var lastSend = DateTime.MinValue;
        long sequence = 0;
        int failures = 0;
        while (_running)
        {
            if (!capture.Read(frame) || frame.Empty())
            {
                Thread.Sleep(15);
                if (++failures >= 100)
                {
                    _running = false;
                    ConnectionLost?.Invoke();
                }
                continue;
            }
            failures = 0;
            PublishPreview(frame);
            var now = DateTime.UtcNow;
            if (now - lastSend < sendInterval) continue;
            lastSend = now;
            Cv2.ImEncode(".jpg", frame, out var jpeg,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, ClientSettings.FrameJpegQuality));
            FrameReady?.Invoke(new FrameMetaPayload(++sequence, DateTimeOffset.UtcNow, frame.Width, frame.Height), jpeg);
        }
    }

    private void PublishPreview(Mat frame)
    {
        try
        {
            var preview = frame.ToBitmapSource();
            preview.Freeze();
            PreviewReady?.Invoke(preview);
        }
        catch (Exception)
        {
            // 손상된 한 프레임은 버리고 다음 프레임을 계속 처리한다.
        }
    }

    public async Task StopAsync()
    {
        _running = false;
        var capture = _capture;
        var thread = _captureThread;
        _capture = null;
        _captureThread = null;
        bool stopped = thread is null || await Task.Run(() => thread.Join(2000)).ConfigureAwait(false);
        if (stopped)
        {
            capture?.Release();
            capture?.Dispose();
        }
        else if (thread is not null && capture is not null)
        {
            _ = Task.Run(() =>
            {
                thread.Join();
                capture.Release();
                capture.Dispose();
            });
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
    private static int PositiveOrDefault(int value, int fallback) => value > 0 ? value : fallback;
}
