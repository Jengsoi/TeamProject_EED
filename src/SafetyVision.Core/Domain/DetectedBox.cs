namespace SafetyVision.Core.Domain;

// Coordinates are in original captured-frame pixel space (post letterbox inverse transform), top-left origin.
public readonly record struct DetectedBox(DetectedClass Class, float X, float Y, float Width, float Height, float Confidence)
{
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;
}
