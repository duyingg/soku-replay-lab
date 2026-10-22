namespace SokuReplayLab;

public sealed record EscapeAttemptResult(int Start, int End, int Round,
    int Defender, int PressureStart, string Outcome);

public static class EscapeAttemptDetector
{
    private const int Window = 30;
    private static readonly HashSet<int> HitActions =
    [50, 51, 52, 53, 56, 57, 58, 62, 63, 64, 70, 71, 72, 73, 74, 75,
     76, 77, 78, 88, 89, 97, 98, 99, 100, 143, 145];

    private sealed class Attempt(int start, int trigger)
    {
        public int Start = start, End = start, Trigger = trigger;
        public int? Recovery;
        public bool Activated;
        public string Outcome = "待判定";
    }
    private sealed class Episode(int start, int round, int defender)
    {
        public int Start = start, End = start, Round = round, Defender = defender;
        public readonly List<Attempt> Attempts = [];
    }

    private static bool Guard(int action) => action is >= 150 and <= 166;
    private static bool Hit(int action) => HitActions.Contains(action);
    private static bool Dd(int action) => action is 220 or 222 or 223 or 224 or 225 or 226;
    private static bool MatchesTrigger(Attempt attempt, Fighter fighter, Fighter old)
    {
        var trigger = attempt.Trigger;
        if ((trigger & 0x70) != 0 && fighter.Action is >= 300 and < 600) return true;
        if ((trigger & 0x200) != 0 && fighter.Action >= 600) return true;
        if ((trigger & 0x80) != 0 && fighter.Action is >= 197 and <= 226) return true;
        if ((trigger & 0x01) != 0 &&
            (fighter.Action is 6 or 7 or 8 or 9 || fighter.Y > old.Y + 0.05))
            return true;
        if ((trigger & 0x0c) != 0 && fighter.Action is (>= 4 and <= 9 or >= 197 and <= 226))
            return true;
        return false;
    }
    private static bool EscapePress(int pressed, Fighter fighter, bool guarding)
    {
        if (!guarding) return (pressed & (0x70 | 0x80 | 0x01 | 0x200)) != 0;
        var forward = fighter.Direction == 1 ? 0x08 : 0x04;
        return (pressed & (0x70 | 0x01 | 0x200 | forward)) != 0;
    }
    private static bool BlockContact(Frame old, Frame frame, int side)
    {
        var current = frame.Side(side);
        var prior = old.Side(side);
        var attacker = frame.Side(1 - side);
        var oldAttacker = old.Side(1 - side);
        return Guard(current.Action) && (!Guard(prior.Action) ||
            current.ActionFrame < prior.ActionFrame && current.ActionFrame <= 2 &&
            (current.Spirit < prior.Spirit || current.HitStop > prior.HitStop ||
             attacker.Collision == 2 && oldAttacker.Collision != 2 ||
             current.Action != prior.Action));
    }

    public static IReadOnlyList<EscapeAttemptResult> Detect(IReadOnlyList<Frame> frames,
        IReadOnlyList<Pressure> pressures)
    {
        var episodes = new List<Episode>();
        for (var side = 0; side < 2; side++)
        {
            Episode? active = null;
            Frame? previous = null;
            var lastGuard = -1000;
            int? lastD = null;
            foreach (var frame in frames)
            {
                if (frame.MatchState != 2 || frame.Round < 0 ||
                    previous is not null && frame.Round != previous.Round)
                {
                    if (active is not null) active.End = previous?.Ordinal ?? frame.Ordinal;
                    active = null;
                    previous = null;
                    lastGuard = -1000;
                    lastD = null;
                    if (frame.MatchState != 2 || frame.Round < 0) continue;
                }
                var fighter = frame.Side(side);
                var old = previous?.Side(side);
                var guard = Guard(fighter.Action);
                var hit = Hit(fighter.Action);
                if (active is not null)
                {
                    foreach (var attempt in active.Attempts.Where(x => x.Outcome == "待判定"))
                    {
                        attempt.End = frame.Ordinal;
                        var origin = attempt.Recovery ?? attempt.Start;
                        var reblocked = previous is not null && guard &&
                            frame.Ordinal > attempt.Start && BlockContact(previous, frame, side);
                        if ((hit || reblocked) && frame.Ordinal - origin <= Window)
                            attempt.Outcome = "失败";
                        else if (attempt.Activated && attempt.Recovery is not null &&
                                 frame.Ordinal - origin >= Window)
                            attempt.Outcome = "成功";
                    }
                }
                if (guard && (old is null || !Guard(old.Action)))
                {
                    if (active is not null) active.End = frame.Ordinal - 1;
                    active = new Episode(frame.Ordinal, frame.Round, side);
                    episodes.Add(active);
                }
                if (guard) lastGuard = frame.Ordinal;
                if (active is not null)
                {
                    active.End = frame.Ordinal;
                    var pressed = (fighter.Input & 0x3ff) & ~(old?.Input & 0x3ff ?? 0);
                    var ddPressed = false;
                    if ((pressed & 0x80) != 0)
                    {
                        if (lastD is int priorD && frame.Ordinal - priorD <= 20 &&
                            (fighter.Input & 0x0f) != 0)
                        { ddPressed = true; lastD = null; }
                        else lastD = frame.Ordinal;
                    }
                    var confirmedDd = Dd(fighter.Action) && old is not null &&
                        !Dd(old.Action) && !active.Attempts.Any(x =>
                            frame.Ordinal - x.Start <= 20);
                    var movingOut = old is not null && Guard(old.Action) && !guard &&
                        fighter.Action is 4 or 5 or 6 or 7 or 8 or 200 or 201 or 202 or
                            203 or 208 or 209 or 210 or 214 &&
                        (fighter.Input & 0x8f) != 0 &&
                        !active.Attempts.Any(x => (x.Trigger & fighter.Input & 0x0d) != 0);
                    if (frame.Ordinal - lastGuard <= Window &&
                        (EscapePress(pressed & ~0x80, fighter, guard) ||
                         ddPressed || confirmedDd || movingOut))
                    {
                        var trigger = (pressed != 0 ? pressed : fighter.Input & 0x8f) |
                            (ddPressed || confirmedDd ? 0x80 : 0);
                        var ongoing = active.Attempts.LastOrDefault(x =>
                            x.Outcome == "待判定" && !x.Activated &&
                            frame.Ordinal - x.Start <= 8);
                        if (ongoing is not null) ongoing.Trigger |= trigger;
                        else active.Attempts.Add(new Attempt(frame.Ordinal, trigger));
                    }
                    var actionStarted = old is not null &&
                        (fighter.Action != old.Action ||
                         fighter.ActionFrame < old.ActionFrame && fighter.ActionFrame <= 2);
                    var activated = !guard && !hit && actionStarted &&
                        (fighter.Action is >= 4 and <= 9 or >= 197 and <= 226 ||
                         fighter.Action >= 300 || fighter.Y > old!.Y + 0.05);
                    foreach (var attempt in active.Attempts.Where(x => x.Outcome == "待判定"))
                    {
                        if (activated && frame.Ordinal - attempt.Start <= Window &&
                            MatchesTrigger(attempt, fighter, old!))
                            attempt.Activated = true;
                        if (attempt.Activated && attempt.Recovery is null &&
                            !guard && !hit && fighter.HitStop == 0 && fighter.Action <= 10 &&
                            !activated)
                            attempt.Recovery = frame.Ordinal;
                    }
                    if (hit || frame.Ordinal - lastGuard > Window &&
                        active.Attempts.All(x => x.Outcome != "待判定" ||
                            frame.Ordinal - x.Start > Window * 3))
                        active = null;
                }
                previous = frame;
            }
        }
        var results = new List<EscapeAttemptResult>();
        foreach (var episode in episodes)
        {
            var pressure = pressures.FirstOrDefault(x => x.Round == episode.Round &&
                x.Defender == episode.Defender && episode.Start >= x.Start &&
                episode.Start <= x.End);
            if (pressure is null) continue;
            // A pressure ends once. Credit only the nearest matching action;
            // an earlier button press cannot inherit a later DD or counter hit.
            bool ExplainsEnd(Attempt attempt) =>
                attempt.Outcome == "成功" && attempt.Start <= pressure.End &&
                pressure.End - attempt.Start <= Window * 2 &&
                ((attempt.Trigger & 0x80) != 0 && pressure.EndReason == "dd" ||
                 (attempt.Trigger & 0x01) != 0 && pressure.EndReason == "跳走" ||
                 (attempt.Trigger & (0x70 | 0x200)) != 0 &&
                 pressure.EndReason is ("被反击" or "转防"));
            var credited = episode.Attempts.Where(ExplainsEnd)
                .OrderByDescending(x => x.Start).FirstOrDefault();
            results.AddRange(episode.Attempts.Where(x => x.Start <= pressure.End)
                .Select(x => new EscapeAttemptResult(
                x.Start, x.End, episode.Round, episode.Defender,
                pressure.Start, x.Outcome == "成功" && x != credited
                    ? "待判定" : x.Outcome)));
        }
        return results.OrderBy(x => x.Start).ThenBy(x => x.Defender).ToArray();
    }
}
