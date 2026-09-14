using OpenCvSharp;

namespace SafetyVision.Client.Services;

/// <summary>
/// 로컬에서 사용할 수 있는 카메라 장치를 찾습니다.
/// </summary>
public static class CameraProbe
{
    public static Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(int maxIndex = 7, CancellationToken ct = default)
    {
        // 존재하지 않는 장치의 Open은 오래 걸릴 수 있으므로 UI 스레드에서 실행하지 않는다.
        return Task.Run<IReadOnlyList<CameraDeviceInfo>>(() => Enumerate(maxIndex, ct), CancellationToken.None);
    }

    private static IReadOnlyList<CameraDeviceInfo> Enumerate(int maxIndex, CancellationToken ct)
    {
        var devices = new List<CameraDeviceInfo>();
        if (maxIndex < 0) return devices;

        for (var index = 0; index <= maxIndex; index++)
        {
            ct.ThrowIfCancellationRequested();

            VideoCapture? capture = null;
            try
            {
                capture = new VideoCapture(index);
                var opened = capture.IsOpened();
                if (!opened) continue;

                ct.ThrowIfCancellationRequested();
                var width = (int)capture.Get(VideoCaptureProperties.FrameWidth);
                var height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
                devices.Add(new CameraDeviceInfo(index, opened, width, height));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 개별 장치 드라이버의 오류는 다른 인덱스 탐색을 막지 않는다.
            }
            finally
            {
                // 탐색이 끝난 장치는 반드시 해제하여 현장 점검 화면이 즉시 열 수 있게 한다.
                if (capture is not null)
                {
                    try { capture.Release(); }
                    catch (Exception) { }

                    try { capture.Dispose(); }
                    catch (Exception) { }
                }
            }
        }

        return devices;
    }

    public sealed record CameraDeviceInfo(int Index, bool Opened, int Width, int Height);
}
