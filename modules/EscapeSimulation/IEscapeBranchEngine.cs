namespace SokuReplayLab;

/// <summary>Host-owned game execution. Calls are sequential; the host owns its lifetime.</summary>
public interface IEscapeBranchEngine
{
    /// <summary>Whether another trial can run after a recoverable error.</summary>
    bool CanContinue { get; }
    Task StartAsync(int requiredFrame);
    Task<EscapeBranch> RunAsync(EscapeInputPlan plan);
    Task RestoreAsync(int cursor);
}

/// <summary>Frames use absolute replay ordinals, including the restored anchor.</summary>
public sealed record EscapeBranch(IReadOnlyList<Frame> Frames, string ReplayPath,
    int Anchor, int End, int MatchedInputFrames);
