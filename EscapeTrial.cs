namespace SokuReplayLab;

public sealed record EscapeVerdict(string Result, string Reason);
public sealed record EscapeTrialResult(int Anchor, int Defender, string Move,
    EscapeVerdict Verdict, ReplaySession Original, string ReplayPath,
    int End, int MatchedInputFrames);

/// <summary>Plans a small set of explicit inputs and judges only observed engine outcomes.</summary>
public static class EscapeTrial
{
    public static readonly string[] Moves = ["4A", "2A", "8", "28", "B", "2B", "2C",
        "DA", "DB", "6D 4A", "1DD", "2DD", "SC", "铜钱 8", "铜钱 28", "铜钱 DD"];
    public static IReadOnlyList<string> AvailableMoves(int character) => Moves.Concat(
        ActionReference.Skills.Where(x => x.Key.Character == character)
            .SelectMany(x => new[] { x.Value.Input + "B", x.Value.Input + "C" }))
        .Distinct().ToArray();
    public static bool IsAvailable(int character, string move)
    {
        if (Moves.Contains(move)) return true;
        try {
            if (move is "SC" or "CARD") return true;
            if (move.StartsWith("铜钱 ", StringComparison.Ordinal))
                return move[3..] is "8" or "28" or "DD" ||
                    move[3..].Length == 3 && move[3] is >= '1' and <= '9' &&
                    move.EndsWith("DD", StringComparison.Ordinal);
            if (IsDashFollowup(move)) { _ = DashPrefix(move, 1, character, 8, 90); return true; }
            _ = CommandPrefix(move, 1, character); return true;
        }
        catch (ArgumentException) { return false; }
    }
    public static bool CanAttempt(PressureGap gap, string move, int character)
    {
        // Only require an observed input window here. Startup and recovery are
        // judged from the game branch, not used to discard possible attempts.
        return move.StartsWith("铜钱 ", StringComparison.Ordinal) ||
            move.EndsWith("DD", StringComparison.Ordinal) || gap.Length > 0;
    }
    public static IReadOnlyList<PressureGap> EligibleGaps(ReplaySession session,
        int defender, string move, int minPressureMoves)
    {
        if (session.Frames.Count == 0) return [];
        return session.PressureGaps.Where(gap =>
        {
            if (defender != -1 && gap.Defender != defender ||
                gap.Start < 0 || gap.Start >= session.Frames.Count) return false;
            var anchor = Anchor(gap, move);
            if (anchor < 32 || anchor + 90 >= session.Frames.Count) return false;
            var character = session.Frames[gap.Start].Side(gap.Defender).Character;
            var hand = session.Frames[anchor].Side(gap.Defender).HandIds;
            if (move is "SC" or "CARD" &&
                    (hand is null || !hand.Any(id => id is >= 200 and < 300)) ||
                move.StartsWith("铜钱 ", StringComparison.Ordinal) &&
                    (hand is null || !hand.Contains(12))) return false;
            return IsAvailable(character, move) && CanAttempt(gap, move, character) &&
                session.Pressures.Any(pressure => pressure.Defender == gap.Defender &&
                    pressure.Round == gap.Round &&
                    pressure.ConfirmedMoves >= minPressureMoves &&
                    pressure.Start <= gap.Start && gap.NextContact <= pressure.End);
        }).ToArray();
    }
    private static readonly HashSet<int> HitReaction =
        [50, 51, 52, 53, 56, 57, 58, 62, 63, 64, 70, 71, 72, 73,
         74, 75, 76, 77, 78, 88, 89, 97, 98, 99, 100, 143, 145];
    private static bool Guard(int action) => action is >= 150 and <= 166;
    private static bool Hit(int action) => HitReaction.Contains(action);
    private static bool Dd(int action) => action is 220 or 222 or 223 or 224 or 225 or 226;

    public static ushort[] Plan(IReadOnlyList<Frame> baseline, PressureGap gap,
        string move, int horizon = 90, int? anchorOverride = null,
        int? coinFollowupStart = null, int? dashDelay = null)
    {
        var anchor = anchorOverride ?? Anchor(gap, move);
        if (anchor < 32 || anchor + horizon >= baseline.Count || horizon > 512)
            throw new InvalidOperationException("接触点附近缺少足够的原回放轨迹。");
        var side = gap.Defender;
        var facing = baseline[anchor].Side(side).Direction;
        if (facing is not (1 or -1)) throw new InvalidOperationException("角色朝向未知。");
        var back = facing == 1 ? 4 : 8;
        var prefix = move switch {
            "4A" => new[] { back | 0x10 },
            "2A" => [2 | 0x10],
            "B" => [0x20],
            "2B" => [2 | 0x20],
            "2C" => [2 | 0x40],
            "8" => [1],
            "28" => [2, 1],
            "1DD" => [back | 2 | 0x80, back | 2, back | 2 | 0x80],
            "2DD" => [2 | 0x80, 2, 2 | 0x80],
            "SC" or "CARD" => CardPrefix(baseline[anchor].Side(side), spell: true),
            _ when move.StartsWith("铜钱 ", StringComparison.Ordinal) =>
                CardPrefix(baseline[anchor].Side(side), spell: false),
            _ when IsDashFollowup(move) =>
                DashPrefix(move, facing, baseline[anchor].Side(side).Character,
                    dashDelay ?? 8, horizon),
            _ => CommandPrefix(move, facing,
                baseline[anchor].Side(side).Character)
        };
        if (prefix.Length > horizon)
            throw new InvalidOperationException("卡牌切换或输入序列超过推演范围。");
        // Finish the selected attempt with crouch guard, never unrelated REP attacks.
        var masks = Enumerable.Repeat((ushort)(back | 2), horizon).ToArray();
        for (var i = 0; i < prefix.Length; i++) masks[i] = (ushort)prefix[i];
        if (move.StartsWith("铜钱 ", StringComparison.Ordinal) && coinFollowupStart >= 0)
        {
            var followup = CommandPrefix(move[3..] == "DD" ? "4DD" : move[3..],
                facing, baseline[anchor].Side(side).Character);
            var start = Math.Max(prefix.Length, coinFollowupStart.Value);
            if (start + followup.Length + 10 > horizon)
                throw new InvalidOperationException("铜钱后没有足够帧观察后续动作。");
            for (var i = 0; i < followup.Length; i++) masks[start + i] = (ushort)followup[i];
        }
        return masks;
    }

    public static int Anchor(PressureGap gap, string move) =>
        move.EndsWith("DD", StringComparison.Ordinal) &&
        !move.StartsWith("铜钱 ", StringComparison.Ordinal) ? gap.NextContact - 3 :
        gap.Length > 0 ? gap.Start - 1 : gap.Start;

    public static int CardPrefixLength(Fighter fighter, bool spell)
    {
        var hand = fighter.HandIds ?? [];
        var index = Array.FindIndex(hand, id => spell ? id is >= 200 and < 300 : id == 12);
        if (index < 0) throw new InvalidOperationException(spell
            ? "当前手牌没有可用符卡。" : "当前手牌没有铜钱。");
        return index * 2 + 2;
    }

    public static int? CoinFollowupOffset(IReadOnlyList<Frame> branch,
        int anchor, int defender, int cardPrefixLength, int horizon)
    {
        var round = branch[anchor].Round;
        for (var index = anchor + cardPrefixLength + 1;
             index <= anchor + horizon - 12 && index < branch.Count; index++)
        {
            var frame = branch[index];
            if (frame.Round != round || frame.MatchState != 2) return null;
            var fighter = frame.Side(defender);
            if (!Guard(fighter.Action) && !Hit(fighter.Action) && fighter.HitStop == 0)
                return index - anchor;
        }
        return null;
    }

    private static int[] CardPrefix(Fighter fighter, bool spell)
    {
        var result = new int[CardPrefixLength(fighter, spell)];
        for (var i = 0; i < result.Length - 2; i += 2) result[i] = 0x100;
        result[^2] = 0x200;
        return result;
    }

    public static bool IsDashFollowup(string move)
    {
        if (move is "DA" or "DB" or "DC") return true;
        var parts = move.Split(' ');
        return parts.Length == 2 && parts[0].Length == 2 &&
            parts[0][0] is >= '1' and <= '9' && parts[0][0] != '5' &&
            parts[0][1] == 'D' && parts[1].Length is >= 1 and <= 2 &&
            parts[1][^1] is 'A' or 'B' or 'C' &&
            parts[1][..^1].All(ch => ch is >= '1' and <= '9');
    }

    public static IReadOnlyList<int> SafeDashReleaseOffsets(IReadOnlyList<Frame> branch,
        int anchor, int defender, int horizon)
    {
        var result = new List<int>();
        var firstHit = -1;
        for (var index = anchor + 1; index < branch.Count && index <= anchor + horizon; index++)
        {
            var own = branch[index].Side(defender);
            if (Hit(own.Action) && own.Hp < branch[index - 1].Side(defender).Hp)
            { firstHit = index; break; }
        }
        for (var delay = 8; delay <= horizon - 12 && anchor + delay < branch.Count; delay++)
        {
            var recent = branch.Skip(anchor + delay - 2).Take(3)
                .Select(frame => frame.Side(defender)).ToArray();
            if (branch[anchor + delay].Round != branch[anchor].Round ||
                branch[anchor + delay].MatchState != 2) break;
            // Any earlier hit invalidates this release: the prefix is identical.
            if (firstHit >= 0 && anchor + delay >= firstHit)
                break;
            if (recent.Length != 3 || recent.Any(f => f.Collision == 6 || Hit(f.Action)))
                continue;
            result.Add(delay);
        }
        return result;
    }

    public static bool HitImmediatelyAfterRelease(IReadOnlyList<Frame> branch,
        int anchor, int defender, int delay)
    {
        if (anchor + delay + 6 >= branch.Count) return true;
        var before = branch[anchor + delay - 1].Side(defender).Hp;
        return branch.Skip(anchor + delay).Take(6).Any(frame => {
            var fighter = frame.Side(defender);
            return fighter.Hp < before && Hit(fighter.Action);
        });
    }

    private static int[] DashPrefix(string move, int facing, int character,
        int delay, int horizon)
    {
        var dashDirection = move.StartsWith('D') ? '6' : move[0];
        var dash = DirectionMask(dashDirection, facing) | 0x80;
        if (delay == -1) return Enumerable.Repeat(dash, horizon).ToArray();
        if (delay is < 8 or > 78) throw new ArgumentOutOfRangeException(nameof(delay));
        var prefix = Enumerable.Repeat(dash, delay).ToArray();
        int[] followup = move.StartsWith('D')
            ? [dash | (move[^1] switch { 'A' => 0x10, 'B' => 0x20, _ => 0x40 })]
            : CommandPrefix(move.Split(' ')[1], facing, character);
        return [..prefix, ..followup];
    }

    private static int[] CommandPrefix(string move, int facing, int character)
    {
        if (move.Length == 3 && move[0] is >= '1' and <= '9' &&
            move.EndsWith("DD", StringComparison.Ordinal))
        {
            var direction = DirectionMask(move[0], facing);
            return [direction | 0x80, direction, direction | 0x80];
        }
        if (move is "A" or "B" or "C" or "D")
            return [move[0] switch { 'A' => 0x10, 'B' => 0x20,
                'C' => 0x40, _ => 0x80 }];
        var jump = move.StartsWith('J');
        var command = jump ? move[1..] : move;
        if (jump && command is "A" or "B" or "C")
            return [..Enumerable.Repeat(DirectionMask('8', facing), 4),
                command[0] switch { 'A' => 0x10, 'B' => 0x20, _ => 0x40 }];
        if (command.Length > 0 && command.All(ch => ch is >= '1' and <= '9'))
        {
            var directions = new List<int>();
            if (jump) directions.AddRange(Enumerable.Repeat(DirectionMask('8', facing), 4));
            foreach (var digit in command) directions.Add(DirectionMask(digit, facing));
            return directions.ToArray();
        }
        if (command.Length < 2 || command[^1] is not ('A' or 'B' or 'C') ||
            !command[..^1].All(ch => ch is >= '1' and <= '9'))
            throw new ArgumentException("暂不支持该逃压输入。");
        var numbers = command[..^1];
        if (numbers.Length > 1 && !ActionReference.Skills.Any(x =>
                x.Key.Character == character && x.Value.Input == numbers))
            throw new ArgumentException("当前角色没有该指令对应的必杀技。");
        if (numbers.Length == 1 && numbers[0] == '1')
            throw new ArgumentException("该方向没有独立普通技。");
        var button = command[^1] switch { 'A' => 0x10, 'B' => 0x20, _ => 0x40 };
        var result = new List<int>();
        if (jump) result.AddRange(Enumerable.Repeat(1, 4));
        var previous = '\0';
        foreach (var number in numbers)
        {
            if (number == previous) result.Add(0);
            result.Add(DirectionMask(number, facing));
            previous = number;
        }
        result[^1] |= button;
        return result.ToArray();
    }

    private static int DirectionMask(char number, int facing)
    {
        var back = facing == 1 ? 4 : 8;
        var front = facing == 1 ? 8 : 4;
        return number switch {
            '1' => back | 2, '2' => 2, '3' => front | 2,
            '4' => back, '5' => 0, '6' => front,
            '7' => back | 1, '8' => 1, '9' => front | 1,
            _ => throw new ArgumentException("无效方向输入。")
        };
    }

    private static bool ExpectedAttack(int action, int character, string move)
    {
        if (IsDashFollowup(move)) move = move.StartsWith('D')
            ? "66" + move[^1] : move.Split(' ')[1];
        if (move is "SC" or "CARD") return action is >= 600 and <= 619;
        if (move.StartsWith("铜钱 ", StringComparison.Ordinal)) return false;
        var notation = ActionCatalog.Display(action, character);
        if (move is "4A" && action is 300 or 330) return true;
        if (move == "B" && action == 400 || move == "C" && action == 410) return true;
        if (string.Equals(notation, move, StringComparison.OrdinalIgnoreCase)) return true;
        var command = move.TrimStart('J');
        if (command.Length < 3 || action is < 500 or > 593) return false;
        var slot = (action - 500) / 20;
        var button = (action - 500) % 5;
        return ActionReference.Skills.TryGetValue((character, slot), out var skill) &&
            skill.Input == command[..^1] && (command[^1] == 'B'
                ? button is 0 or 2 : command[^1] == 'C' && button is 1 or 3);
    }

    public static EscapeVerdict Judge(IReadOnlyList<Frame> baseline,
        IReadOnlyList<Frame> branch, int anchor, int defender, string move, int horizon)
    {
        if (defender is not (0 or 1) || horizon < 1 || anchor < 0 || anchor + horizon >= baseline.Count ||
            anchor + horizon >= branch.Count)
            return new("未判定", "缺少相同长度的原回放或分支轨迹");
        var initial = baseline[anchor];
        if (initial.MatchState != 2 || branch[anchor].Round != initial.Round ||
            branch[anchor].EngineFrame != initial.EngineFrame ||
            Enumerable.Range(0, 2).Any(side => {
                var expected = initial.Side(side);
                var actual = branch[anchor].Side(side);
                return expected.Character != actual.Character || expected.Hp != actual.Hp ||
                    expected.Action != actual.Action || expected.ActionFrame != actual.ActionFrame ||
                    Math.Abs(expected.X - actual.X) > 0.1 || Math.Abs(expected.Y - actual.Y) > 0.1;
            })) return new("未判定", "分支起点与原回放不一致");
        var previousOwn = initial.Side(defender);
        var previousEnemy = initial.Side(1-defender);
        var lastEnemy = previousEnemy;
        var lastBaseEnemy = previousEnemy;
        var ownHit = false;
        var enemyHit = false;
        var enemyBlocked = false;
        var escapeStarted = false;
        var recovered = false;
        var crossedRound = false;
        var stateDiverged = false;
        var jump = move is "8" or "28" or "7" or "9" or
            "铜钱 8" or "铜钱 28" || move.StartsWith('J');
        var dd = move.EndsWith("DD", StringComparison.Ordinal);
        var anyMove = false;
        var attackStarted = false;
        var recoveryFrame = -1;
        var guardedAgain = false;
        var attributionLost = false;
        // Damage is attributable only after the requested move really starts.
        // A later unrelated attack invalidates attribution (including old projectiles).
        for (var i = anchor + 1; i <= anchor + horizon; i++)
        {
            var frame = branch[i];
            if (frame.Round != initial.Round || frame.MatchState != 2)
            {
                crossedRound = true;
                break;
            }
            var own = frame.Side(defender);
            var enemy = frame.Side(1-defender);
            var baseOwn = baseline[i].Side(defender);
            var baseEnemy = baseline[i].Side(1-defender);
            var requestedAttack = ExpectedAttack(own.Action, own.Character, move);
            if (requestedAttack && (own.Action != previousOwn.Action ||
                own.ActionFrame < previousOwn.ActionFrame)) attackStarted = true;
            if (attackStarted && own.Action >= 300 && !requestedAttack)
                attributionLost = true;
            stateDiverged |= own.Action != baseOwn.Action || enemy.Action != baseEnemy.Action ||
                own.Hp != baseOwn.Hp || enemy.Hp != baseEnemy.Hp ||
                Math.Abs(own.X - baseOwn.X) > 0.05 || Math.Abs(own.Y - baseOwn.Y) > 0.05;
            ownHit |= (own.Hp < previousOwn.Hp || enemy.ComboDamage > previousEnemy.ComboDamage) &&
                (Hit(own.Action) || enemy.Collision == 1 ||
                 enemy.ComboDamage > previousEnemy.ComboDamage);
            enemyHit |= attackStarted && !attributionLost &&
                (enemy.Hp < previousEnemy.Hp || own.ComboDamage > previousOwn.ComboDamage) &&
                enemy.Hp < baseEnemy.Hp &&
                (Hit(enemy.Action) || own.Collision == 1 ||
                 own.ComboDamage > previousOwn.ComboDamage);
            enemyBlocked |= attackStarted && !attributionLost && Guard(enemy.Action) &&
                (!Guard(previousEnemy.Action) || enemy.ActionFrame < previousEnemy.ActionFrame &&
                 enemy.ActionFrame <= 2 && (enemy.Spirit < previousEnemy.Spirit ||
                 enemy.HitStop > previousEnemy.HitStop || own.Collision == 2 &&
                 previousOwn.Collision != 2 || enemy.Action != previousEnemy.Action)) &&
                (own.Collision == 2 || own.Action >= 300) &&
                !(Guard(baseEnemy.Action) && baseOwn.Collision == 2);
            anyMove |= attackStarted;
            var executing = dd ? Dd(own.Action) : jump &&
                (own.Action is 6 or 7 or 8 or 9 or 208 or 209 or 210 or 211 or 212 ||
                 own.Y > initial.Side(defender).Y + 0.05);
            if (executing) escapeStarted = true;
            else if (escapeStarted && own.HitStop == 0 && !Hit(own.Action) &&
                     (dd && !Dd(own.Action) || jump && own.Y <= 0.1 && own.Action <= 10))
            {
                recovered = true;
                if (recoveryFrame < 0) recoveryFrame = i;
            }
            if (escapeStarted && Guard(own.Action)) guardedAgain = true;
            previousOwn = own;
            previousEnemy = enemy;
            lastEnemy = enemy;
            lastBaseEnemy = baseEnemy;
        }
        if (!stateDiverged)
            return new("未判定", "分支与原回放的战斗状态一致，无法归因于本次输入");
        if (ownHit)
            return new("失败", enemyHit ? "被对手命中；双方互击" : "逃压后仍被对手命中");
        if (enemyHit && lastEnemy.Hp < lastBaseEnemy.Hp)
            return new("成功", $"对应招式已发动并新增命中；{horizon} 帧观察内未受击");
        if (enemyBlocked) return new("成功", $"对应招式已发动并打防；{horizon} 帧观察内未受击");
        if (dd || jump)
        {
            if (!escapeStarted)
                return crossedRound ? new("未判定", "动作发动前回合结束") :
                    new("失败", "输入未发动对应动作");
            if (guardedAgain) return new("失败", "动作已发动，但观察内再次进入防御，未脱离压制");
            return recovered && !crossedRound && anchor + horizon - recoveryFrame >= 12
                ? new("成功", "动作已恢复，随后至少 12 帧且至观察结束未受击或再次防御")
                : new("未判定", "已发动，但恢复后的安全观察不足");
        }
        if (crossedRound) return new("未判定", "形成接触前回合结束");
        return anyMove ? new("失败", "已出招，但未确认命中或打防") :
            new("失败", "未观察到对应招式发动");
    }
}
