namespace SokuReplayLab;

public sealed record EscapeVerdict(string Result, string Reason);
public sealed record EscapeTrialResult(int Anchor, int Defender, string Move,
    EscapeVerdict Verdict, ReplaySession Original, string ReplayPath,
    int End, int MatchedInputFrames)
{
    public int PressureStart { get; init; } = -1;
    public int PressureEnd { get; init; } = -1;
    public int AttemptCount { get; init; }
}

