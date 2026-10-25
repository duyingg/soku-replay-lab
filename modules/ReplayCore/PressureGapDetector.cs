namespace SokuReplayLab;

public sealed record PressureGap(int Round, int Defender, int Start, int NextContact,
    int Length, string Source, bool MashableZero);

/// <summary>Finds observable free frames between two confirmed guard contacts.</summary>
public static class PressureGapDetector
{
    private const int GuardAvailable = 0x10;
    private const int Guarding = 0x800;
    private static readonly IReadOnlyDictionary<int, int> LastGuardFrame =
        new Dictionary<int, int> {
            [150] = 9, [154] = 9, [151] = 15, [155] = 15,
            [152] = 21, [156] = 21, [153] = 27, [157] = 27,
            [158] = 19, [159] = 11, [160] = 19, [161] = 27,
            [164] = 19, [165] = 27
        };
    private static readonly HashSet<int> HitReaction =
        [50, 51, 52, 53, 56, 57, 58, 62, 63, 64, 70, 71, 72, 73,
         74, 75, 76, 77, 78, 88, 89, 97, 98, 99, 100, 143, 145];

    private static bool Guard(int action) => action is >= 150 and <= 166;
    private static bool Actionable(int action) => action <= 10 || action is 180 or 181 ||
        action is >= 197 and <= 226 || action >= 300;

    private static bool Contact(Frame? previous, Frame current, int defender)
    {
        var fighter = current.Side(defender);
        var attacker = current.Side(1 - defender);
        if (!Guard(fighter.Action)) return false;
        if (previous is null) return true;
        var old = previous.Side(defender);
        var oldAttacker = previous.Side(1 - defender);
        if (!Guard(old.Action)) return true;
        return fighter.ActionFrame < old.ActionFrame && fighter.ActionFrame <= 2 &&
            (fighter.Spirit < old.Spirit || fighter.HitStop > old.HitStop ||
             attacker.Collision == 2 && oldAttacker.Collision != 2 ||
             fighter.Action != old.Action);
    }

    public static IReadOnlyList<PressureGap> Detect(IReadOnlyList<Frame> frames)
    {
        var result = new List<PressureGap>();
        int?[] last = [null, null];
        for (var index = 0; index < frames.Count; index++)
        {
            var current = frames[index];
            if (current.MatchState != 2) { last = [null, null]; continue; }
            var previous = index > 0 && frames[index - 1].MatchState == 2 &&
                frames[index - 1].Round == current.Round ? frames[index - 1] : null;
            for (var side = 0; side < 2; side++)
            {
                if (HitReaction.Contains(current.Side(side).Action)) { last[side] = null; continue; }
                if (!Contact(previous, current, side)) continue;
                if (last[side] is int before && frames[before].Round == current.Round)
                {
                    var allFlags = true;
                    for (var j = before + 1; j < index; j++)
                        if (frames[j].Side(side).FrameFlags < 0) { allFlags = false; break; }
                    if (index == before + 1 && previous is not null)
                        allFlags = previous.Side(side).FrameFlags >= 0 &&
                            current.Side(side).FrameFlags >= 0;
                    var runs = new List<List<int>>();
                    if (allFlags)
                    {
                        List<int>? run = null;
                        for (var j = before + 1; j < index; j++)
                        {
                            var flags = frames[j].Side(side).FrameFlags;
                            if ((flags & GuardAvailable) != 0 && (flags & Guarding) == 0)
                                (run ??= []).Add(j);
                            else if (run is not null) { runs.Add(run); run = null; }
                        }
                        if (run is not null) runs.Add(run);
                    }
                    else
                    {
                        List<int>? free = null;
                        for (var j = before + 1; j < index; j++)
                        {
                            var fighter = frames[j].Side(side);
                            if (Actionable(fighter.Action) && fighter.HitStop == 0)
                                (free ??= []).Add(j);
                            else if (free is not null)
                            {
                                runs.Add(free);
                                free = null;
                            }
                        }
                        if (free is not null) runs.Add(free);
                    }
                    if (runs.Count == 0) runs.Add([]);
                    foreach (var free in runs.Where(x => x.Count <= 30))
                    {
                        var start = free.Count == 0 ? before : free[0];
                        var old = previous?.Side(side);
                        var mashableZero = free.Count == 0 && allFlags && old is not null &&
                            LastGuardFrame.TryGetValue(old.Action, out var lastFrame) &&
                            old.ActionFrame == lastFrame;
                        result.Add(new(current.Round, side, frames[start].Ordinal,
                            current.Ordinal, free.Count,
                            mashableZero ? "防御动作末帧估计" :
                            allFlags ? "引擎防御帧标志" : "动作状态推断", mashableZero));
                    }
                }
                last[side] = index;
            }
        }
        return result.OrderBy(x => x.Start).ThenBy(x => x.Defender).ToArray();
    }

    public static int FourAStartup(int character) => character is >= 0 and <= 19
        ? new[] { 8, 7, 8, 9, 9, 7, 7, 10, 9, 9, 7, 7, 9, 8, 9, 7, 6, 6, 7, 7 }[character]
        : 5;
}
