using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Analysis;

public static class RoiEvaluator
{
    public static bool IsCenterInRoi(DetectedBox person, int frameWidth, int frameHeight, SafetyVisionOptions options)
    {
        double left = options.RoiLeft * frameWidth;
        double right = options.RoiRight * frameWidth;
        double top = options.RoiTop * frameHeight;
        double bottom = options.RoiBottom * frameHeight;
        double cx = person.CenterX;
        double cy = person.CenterY;
        return cx >= left && cx <= right && cy >= top && cy <= bottom;
    }

    public static bool MeetsMinHeight(DetectedBox person, int frameHeight, SafetyVisionOptions options)
        => person.Height >= frameHeight * options.MinPersonHeightRatio;
}
