using System.Windows.Media.Imaging;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.Camera;

public interface ICameraService : IDisposable
{
    int Width { get; }
    int Height { get; }
    event Action<BitmapSource>? PreviewReady;
    event Action<FrameMetaPayload, byte[]>? FrameReady;
    event Action? ConnectionLost;
    bool Start();
    Task StopAsync();
}
