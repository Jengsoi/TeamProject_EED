using SafetyVision.Core.Configuration;
using SafetyVision.Core.Domain;

namespace SafetyVision.Core.Analysis;

public static class PpeAssociationRules
{
    // 05_AI모델명세.md 5절: Person 원본 박스 (x0,y0,w,h) 대비 PPE 중심(cx,cy) 허용 범위.
    public static bool IsCandidate(DetectedClass ppeClass, DetectedBox person, DetectedBox ppe, SafetyVisionOptions options)
    {
        double x0 = person.X, y0 = person.Y, w = person.Width, h = person.Height;
        double cx = ppe.CenterX, cy = ppe.CenterY;

        bool xOk = cx >= x0 - options.PersonHorizontalMarginRatio * w
                && cx <= x0 + (1 + options.PersonHorizontalMarginRatio) * w;
        if (!xOk) return false;

        return ppeClass switch
        {
            DetectedClass.Hardhat or DetectedClass.NoHardhat =>
                cy >= y0 - options.HeadTopMarginRatio * h && cy <= y0 + options.HardhatBottomRatio * h,
            DetectedClass.Mask or DetectedClass.NoMask =>
                cy >= y0 - options.HeadTopMarginRatio * h && cy <= y0 + options.MaskBottomRatio * h,
            DetectedClass.SafetyVest or DetectedClass.NoSafetyVest =>
                cy >= y0 + options.VestTopRatio * h && cy <= y0 + options.VestBottomRatio * h,
            _ => false
        };
    }
}
