using System.IO;
using System.Windows.Media.Imaging;
using SafetyVision.Protocol.Dto;

namespace SafetyVision.Client.Models;

internal static class InspectionResultMapper
{
    public static IEnumerable<EquipmentDisplayItem> ToDisplayItems(InspectionResultPayload payload) =>
        payload.Items.Select(item => new EquipmentDisplayItem(
            item.EquipmentCode,
            ViewModels.DashboardViewModel.LabelFor(item.EquipmentCode),
            item.Status,
            item.Score));

    public static BitmapImage? ToBitmap(byte[]? image)
    {
        if (image is null) return null;
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(image);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
