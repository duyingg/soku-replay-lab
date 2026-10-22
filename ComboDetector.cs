namespace SokuReplayLab;

/// <summary>Groups confirmed impacts into continuous hit reactions.</summary>
public static class ComboDetector
{
    private static readonly HashSet<int> HitReaction =
    [50, 51, 52, 53, 56, 57, 58, 62, 63, 64, 70, 71, 72, 73, 74, 75,
     76, 77, 78, 88, 89, 97, 98, 99, 100, 143, 145];
    private static bool Hit(int action) => HitReaction.Contains(action);
    private static bool Guard(int action) => action is >= 150 and <= 166;

    private sealed class Active(Frame frame, int round, int side)
    {
        public int Start = frame.Ordinal;
        public int End = frame.Ordinal;
        public int Round = round;
        public int Side = side;
        public int Damage;
        public int Limit;
        public bool Circle;
        public bool Knockdown;
        public string Position = ReplayAnalyzer.BoardPosition(frame.Side(side));
        public List<int> Impacts = [];
        public List<int> Moves = [];

        public void Observe(Frame frame)
        {
            var attacker = frame.Side(Side);
            var defender = frame.Side(1 - Side);
            Circle |= attacker.ComboLimit >= 100 || defender.DamageLimited > 0;
            Knockdown |= defender.Action is >= 97 and <= 99 || defender.Hp <= 0;
        }

        public void Impact(Frame frame, int loss)
        {
            var attacker = frame.Side(Side);
            End = frame.Ordinal;
            Damage += loss;
            Limit = Math.Max(Limit, attacker.ComboLimit);
            Impacts.Add(frame.Ordinal);
            if (Moves.Count == 0 || Moves[^1] != attacker.Action) Moves.Add(attacker.Action);
            Observe(frame);
        }

        public Combo Finish(int recoveryEnd) => new(Start, recoveryEnd, Round, Side,
            Impacts.Count, Damage, Limit, string.Join(" → ", Moves), Position,
            Circle, Knockdown, Impacts.ToArray());
    }

    public static IReadOnlyList<Combo> Detect(IReadOnlyList<Frame> frames)
    {
        var byRound = new Dictionary<int, List<Frame>>();
        int? phase = null;
        foreach (var frame in frames)
        {
            if (frame.MatchState == 2 && frame.Round >= 0)
            {
                phase = frame.Round;
                if (!byRound.TryGetValue(phase.Value, out var records))
                    byRound[phase.Value] = records = [];
                records.Add(frame);
            }
            else if (frame.MatchState is 3 or 5 && phase is not null)
                byRound[phase.Value].Add(frame);
        }
        var combos = new List<Combo>();
        foreach (var (round, records) in byRound)
        {
            for (var side = 0; side < 2; side++)
            {
                Active? current = null;
                Frame? previous = null;
                foreach (var frame in records)
                {
                    var attacker = frame.Side(side);
                    var defender = frame.Side(1 - side);
                    if (current is not null)
                    {
                        current.Observe(frame);
                        if (defender.Hp <= 0)
                        {
                            combos.Add(current.Finish(frame.Ordinal));
                            current = null;
                            previous = frame;
                            continue;
                        }
                        if (!Hit(defender.Action))
                        {
                            combos.Add(current.Finish(frame.Ordinal));
                            current = null;
                        }
                    }
                    if (previous is not null)
                    {
                        var oldAttacker = previous.Side(side);
                        var oldDefender = previous.Side(1 - side);
                        var loss = oldDefender.Hp - defender.Hp;
                        var changed = attacker.ComboCount != oldAttacker.ComboCount ||
                            attacker.ComboDamage != oldAttacker.ComboDamage;
                        var impact = oldDefender.Hp > 0 && loss > 0 &&
                            attacker.ComboCount > 0 && attacker.ComboDamage > 0 &&
                            !Guard(defender.Action) &&
                            (changed || current is null && Hit(defender.Action) &&
                                !Hit(oldDefender.Action));
                        if (impact)
                        {
                            var started = new Active(frame, round, side);
                            if (!Hit(defender.Action) && defender.Hp > 0)
                            {
                                started.Impact(frame, loss);
                                // Armor damage without hitstun is one event per impact.
                                started.Knockdown = false;
                                started.Circle = defender.DamageLimited > 0;
                                combos.Add(started.Finish(frame.Ordinal));
                                previous = frame;
                                continue;
                            }
                            current ??= started;
                            current.Impact(frame, loss);
                        }
                    }
                    previous = frame;
                }
                if (current is not null)
                    combos.Add(current.Finish(records[^1].Ordinal));
            }
        }
        return combos.OrderBy(x => x.Start).ThenBy(x => x.Side).ToArray();
    }
}
