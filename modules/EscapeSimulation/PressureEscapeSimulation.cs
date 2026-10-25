using System.IO;

namespace SokuReplayLab;

public static class PressureSelection
{
    public static IReadOnlyList<Pressure> Select(ReplaySession replay, int minimumMoves) =>
        replay.Pressures.Where(p => p.ConfirmedMoves >= minimumMoves)
            .OrderBy(p => p.Start).ThenBy(p => p.Defender).ToArray();
}

public sealed record PressureEscapeJob(Pressure Pressure, string Move);
public sealed record EscapeInputPlan(int Anchor, int Side, string Move, ushort[] Inputs);

/// <summary>Pressure-based trials: explicit input schedules and observed outcomes.</summary>
public static class PressureEscapeSimulation
{
    public const int ObservationFrames = 124;
    private const int StableFrames = 16;
    public static IReadOnlyList<string> Moves(int character) =>
        new[] { "4A", "2A", "B", "2B", "C", "2C", "8", "28", "1DD", "2DD", "SC" }
            .Concat(ActionReference.Skills.Where(x => x.Key.Character == character)
                .SelectMany(x => new[] { x.Value.Input + "B", x.Value.Input + "C" }))
            .Distinct().ToArray();

    public static IReadOnlyList<PressureEscapeJob> Jobs(ReplaySession source,
        int minimumMoves, IEnumerable<string> moves) =>
        PressureSelection.Select(source, minimumMoves)
            .SelectMany(p => moves.Distinct().Select(move => new PressureEscapeJob(p, move)))
            .ToArray();

    public static bool Guard(int action) => action is >= 150 and <= 166;
    public static bool Hit(int action) => action is 50 or 51 or 52 or 53 or 56 or 57 or 58 or
        62 or 63 or 64 or 70 or 71 or 72 or 73 or 74 or 75 or 76 or 77 or 78 or
        88 or 89 or 97 or 98 or 99 or 100 or 143 or 145;
    private static bool Dd(int action) => action is 220 or 222 or 223 or 224 or 225 or 226;
    private static bool Jump(int action) => action is 6 or 7 or 8 or 9 or 208 or 209 or 210 or 211 or 212;

    public static IReadOnlyList<EscapeInputPlan> Plans(ReplaySession source, PressureEscapeJob job)
    {
        var pressure = job.Pressure;
        if (pressure.Defender is not (0 or 1) || pressure.Start < 1 || pressure.End >= source.Frames.Count)
            return [];
        var result = new List<EscapeInputPlan>();
        var boundaries = new SortedSet<int> { pressure.Start };
        // Sample every observed block contact and every exit from blockstun.
        // The plan never depends on the old gap detector or its zero-gap estimates.
        for (var i = pressure.Start; i <= pressure.End; i++)
        {
            var now = source.Frames[i];
            var old = source.Frames[i - 1];
            if (now.MatchState != 2 || now.Round != pressure.Round) continue;
            var own = now.Side(pressure.Defender);
            var previous = old.Side(pressure.Defender);
            if (Guard(own.Action) && (!Guard(previous.Action) ||
                own.ActionFrame < previous.ActionFrame && own.ActionFrame <= 2) ||
                Guard(previous.Action) && !Guard(own.Action)) boundaries.Add(i);
        }
        foreach (var boundary in boundaries)
        foreach (var shift in new[] { 0, 1, 2 })
        {
            var at = boundary + shift;
            if (at > pressure.End) continue;
            var fighter = source.Frames[at].Side(pressure.Defender);
            int[] command;
            try { command = Encode(job.Move, fighter); }
            catch (InvalidOperationException) { continue; }
            // One neutral frame clears held buttons before the command. Its last
            // input lands at the chosen boundary, allowing buffered motions.
            var anchor = at - command.Length - 1;
            if (anchor < 32 || anchor + ObservationFrames >= source.Frames.Count ||
                source.Frames[anchor].Round != pressure.Round ||
                source.Frames[anchor].MatchState != 2) continue;
            var input = GuardInputs(source, anchor, pressure.Defender, ObservationFrames);
            input[0] = 0;
            for (var i = 0; i < command.Length; i++) input[i + 1] = (ushort)command[i];
            if (!result.Any(p => p.Anchor == anchor && p.Inputs.AsSpan().SequenceEqual(input)))
                result.Add(new(anchor, pressure.Defender, job.Move, input));
        }
        return result.OrderBy(p => p.Anchor).ToArray();
    }

    public static ushort[] GuardInputs(ReplaySession source, int anchor, int side, int length)
    {
        var inputs = new ushort[length];
        for (var i = 0; i < length; i++)
        {
            var fighter = source.Frames[Math.Min(anchor + i + 1, source.Frames.Count - 1)].Side(side);
            var facing = fighter.Direction;
            if (facing is not (1 or -1))
                throw new InvalidOperationException("轨迹缺少朝向，不能可靠生成逃压输入。");
            inputs[i] = (ushort)(2 | (facing == 1 ? 4 : 8));
        }
        return inputs;
    }

    private static int[] Encode(string move, Fighter fighter)
    {
        var back = fighter.Direction == 1 ? 4 : 8;
        var front = fighter.Direction == 1 ? 8 : 4;
        int Direction(char value) => value switch {
            '1' => back | 2, '2' => 2, '3' => front | 2, '4' => back,
            '5' => 0, '6' => front, '7' => back | 1, '8' => 1, '9' => front | 1,
            _ => throw new InvalidOperationException("不支持的方向。")
        };
        if (move == "SC")
        {
            var hand = fighter.HandIds;
            if (hand is null) throw new InvalidOperationException("手牌身份未知。");
            var index = Array.FindIndex(hand, id => id is >= 200 and < 300);
            if (index < 0) throw new InvalidOperationException("当前无符卡。");
            var input = new int[index * 2 + 2];
            for (var i = 0; i < index * 2; i += 2) input[i] = 0x100;
            input[^2] = 0x200;
            return input;
        }
        if (move is "1DD" or "2DD")
        {
            var direction = Direction(move[0]);
            return [direction | 0x80, direction, direction | 0x80];
        }
        if (move is "8" or "28") return move == "8" ? [1, 1, 1] : [2, 2, 1, 1, 1];
        if (move.Length < 1 || move[^1] is not ('A' or 'B' or 'C'))
            throw new InvalidOperationException("本次招式不受支持。");
        var digits = move[..^1];
        if (digits.Length > 1 && !ActionReference.Skills.Any(x =>
            x.Key.Character == fighter.Character && x.Value.Input == digits))
            throw new InvalidOperationException("当前角色没有该必杀指令。");
        var button = move[^1] switch { 'A' => 0x10, 'B' => 0x20, _ => 0x40 };
        if (digits.Length == 0) return [button, button];
        var inputs = new List<int>();
        foreach (var digit in digits)
        {
            if (inputs.Count > 0 && inputs[^1] == Direction(digit)) inputs.Add(0);
            inputs.Add(Direction(digit));
        }
        inputs[^1] |= button;
        return inputs.ToArray();
    }

    public static bool RequestedAction(string move, Fighter fighter)
    {
        if (move is "8" or "28") return Jump(fighter.Action);
        if (move.EndsWith("DD", StringComparison.Ordinal)) return Dd(fighter.Action);
        if (move == "SC") return fighter.Action is >= 600 and <= 619;
        if (move == "4A") return fighter.Action is 300 or 330;
        if (move == "B") return fighter.Action == 400;
        if (move == "C") return fighter.Action == 410;
        if (ActionCatalog.Display(fighter.Action, fighter.Character) == move) return true;
        if (move.Length < 3 || fighter.Action is < 500 or > 593) return false;
        var slot = (fighter.Action - 500) / 20;
        var variant = (fighter.Action - 500) % 5;
        return ActionReference.Skills.TryGetValue((fighter.Character, slot), out var skill) &&
            skill.Input == move[..^1] && (move[^1] == 'B' ? variant is 0 or 2 :
                move[^1] == 'C' && variant is 1 or 3);
    }

    public static EscapeVerdict Evaluate(EscapeInputPlan plan, IReadOnlyList<Frame> attempt,
        IReadOnlyList<Frame> control, int matchedInputs)
    {
        var anchor = plan.Anchor;
        var end = anchor + plan.Inputs.Length;
        if (plan.Side is not (0 or 1) || anchor < 0 || plan.Inputs.Length < 1 || end < anchor ||
            attempt.Count <= end || control.Count <= end ||
            !ReplayState.Matches(attempt[anchor], control[anchor]))
            return new("未判定", "输入、分支长度或对照起点校验未通过");
        if (matchedInputs != plan.Inputs.Length)
            return new("未判定", $"仅验证 {matchedInputs}/{plan.Inputs.Length} 帧输入；回合结束或切换后不能继续确认本次逃压");
        var side = plan.Side;
        var round = attempt[anchor].Round;
        var started = -1;
        var hit = false;
        var counterHit = false;
        var counterBlock = false;
        var blockedAgain = false;
        var safe = 0;
        var terminated = false;
        for (var i = anchor + 1; i <= end; i++)
        {
            var frame = attempt[i];
            var old = attempt[i - 1];
            if (frame.Round != round || frame.MatchState != 2)
            { terminated = true; break; }
            var own = frame.Side(side);
            var previous = old.Side(side);
            var enemy = frame.Side(1 - side);
            var oldEnemy = old.Side(1 - side);
            var reference = control[i];
            if (started < 0 && RequestedAction(plan.Move, own) &&
                (own.Action != previous.Action || own.ActionFrame < previous.ActionFrame)) started = i;
            var ownHit = Hit(own.Action) && (own.Hp < previous.Hp ||
                enemy.ComboDamage > oldEnemy.ComboDamage);
            hit |= ownHit;
            if (started < 0) continue;
            var activeAttack = RequestedAction(plan.Move, own) && own.Action >= 300;
            // The guard-only control rules out damage and contacts from lingering
            // projectiles or attacks that existed before the selected input.
            counterHit |= activeAttack && Hit(enemy.Action) && enemy.Hp < oldEnemy.Hp &&
                (own.Collision == 1 || own.ComboDamage > previous.ComboDamage) &&
                enemy.Hp < reference.Side(1 - side).Hp;
            var guardContact = Guard(enemy.Action) && (!Guard(oldEnemy.Action) ||
                enemy.ActionFrame < oldEnemy.ActionFrame && enemy.ActionFrame <= 2);
            counterBlock |= activeAttack && guardContact && own.Collision == 2 &&
                !(Guard(reference.Side(1 - side).Action) && reference.Side(side).Collision == 2);
            if (i > started && Guard(own.Action)) blockedAgain = true;
            var free = !Hit(own.Action) && !Guard(own.Action) && own.HitStop == 0 &&
                (own.Action <= 10 || own.Action is >= 197 and <= 219);
            safe = free ? safe + 1 : 0;
        }
        if (hit) return new("受击", counterHit ? "逃压过程中互击，不能作为无伤逃压建议" : "本次输入后受到命中");
        if (started < 0) return new(terminated ? "未判定" : "未发动",
            terminated ? "动作发动前对战状态结束" : "输入已验证，但游戏未发动所选招式");
        if (plan.Move == "SC" && !CardTimeline.Build(attempt.Skip(anchor).ToArray()).Any(c =>
            c.Side == side && c.Kind == CardKind.Use && c.CardId is >= 200 and < 300))
            return new("未判定", "观察到符卡动作，但没有确认完成用卡");
        if (terminated) return new("未判定", "观察期提前结束，不能确认后续安全");
        if (blockedAgain) return new("仍受压", "所选招式发动后再次进入防御");
        if (safe < StableFrames) return new("未判定", "动作结束后的连续安全观察不足 16 帧");
        if (counterHit) return new("成功", "所选招式新增命中，随后恢复且观察结束前未受击或再次防御");
        if (counterBlock) return new("成功", "所选招式令对手防御，随后恢复且观察结束前未受击或再次防御");
        if (plan.Move is "8" or "28" or "1DD" or "2DD")
            return new("成功", "所选移动动作已发动，观察结束时连续至少 16 帧可行动且未再次受压");
        return new("未找到", "所选招式发动，但没有确认打断压制或取得攻势");
    }
}

