namespace SokuReplayLab;

/// <summary>REP frame counts use a fixed 62 frames per second timebase.</summary>
public static class ReplayTiming
{
    public const int FramesPerSecond = 62;
    public const double SecondsPerFrame = 1.0 / FramesPerSecond;
}
