namespace SokuReplayLab;

public sealed record InterpretationTip(string Player, string Category,
    string Observation, string Evidence);

/// <summary>Turns observable replay counts into the user's explicit review rules.</summary>
public static class ReplayInterpretation
{
    private static readonly int[] CommonActions =
        [300, 302, 303, 304, 306, 330, 400, 401, 402, 404, 410, 411, 412, 414];
    private static readonly HashSet<string> EscapedPressureEnds =
        ["后续未接", "脱离", "dd", "跳走"];

    public static IReadOnlyList<InterpretationTip> Evaluate(ReplaySession data,
        int minPressureMoves)
    {
        var tips = new List<InterpretationTip>();
        var actionCounts = ActionCounts(data);
        var moveCounts = MoveCounts(data, actionCounts);
        var invalid = new List<string>();
        for (var side = 0; side < 2; side++)
        {
            var player = side == 0 ? "1P" : "2P";
            var stats = data.Total(side);
            if (stats.CounterKnown && stats.TakenHits > 0 &&
                stats.TakenCounterHits * 5 >= stats.TakenHits * 3)
                invalid.Add($"{player} 被康 {stats.TakenCounterHits}/{stats.TakenHits} 次" +
                    $"（{Percent(stats.TakenCounterHits, stats.TakenHits)}）");
        }
        if (invalid.Count > 0)
            tips.Add(new("本局", "对局有效性",
                "被康率达到 60%；双方实力差距可能悬殊，或当时状态不好。本局不纳入有效对局评价。",
                string.Join("；", invalid) + "；被康次数 ÷ 被命中次数 ≥ 60%"));

        for (var side = 0; side < 2; side++)
        {
            var player = side == 0 ? "1P" : "2P";
            var stats = data.Total(side);
            var combos = data.Combos.Where(x => x.Side == side).ToArray();
            var finished = combos.Count(x => x.Circle || x.Knockdown);
            if (combos.Length > 0 && finished * 5 < combos.Length * 3)
                tips.Add(new(player, "连段练习",
                    "出圈或倒地率低于 60%，需要加强连段练习。",
                    $"{finished}/{combos.Length} 段（{Percent(finished, combos.Length)}）；按确认连段计"));

            var applied = data.Pressures.Where(x => x.Attacker == side &&
                x.ConfirmedMoves >= minPressureMoves).ToArray();
            var escaped = applied.Count(x => EscapedPressureEnds.Contains(x.EndReason));
            if (applied.Length > 0 && escaped * 10 >= applied.Length * 9)
                tips.Add(new(player, "压制",
                    "90% 以上的自身压制以后续未接、脱离、dd 或跳走结束，需要加强压制。",
                    $"{escaped}/{applied.Length} 段（{Percent(escaped, applied.Length)}）；" +
                    $"至少 {minPressureMoves} 招"));

            var received = data.Pressures.Where(x => x.Defender == side &&
                x.ConfirmedMoves >= minPressureMoves).ToArray();
            var caught = received.Count(x => x.EndReason is "抓乱动" or "抓dd");
            if (received.Length > 0 && caught * 5 >= received.Length * 4)
                tips.Add(new(player, "逃压",
                    "80% 以上的被压制段以抓乱动或抓 DD 结束，需要加强逃压。",
                    $"{caught}/{received.Length} 段（{Percent(caught, received.Length)}）；" +
                    $"至少 {minPressureMoves} 招"));

            if (stats.WakeupPressures > 0 &&
                stats.WakeupPressureSuccess * 5 < stats.WakeupPressures * 4)
                tips.Add(new(player, "压起身",
                    "压起身成功率低于 80%，需要加强压起身。",
                    $"{stats.WakeupPressureSuccess}/{stats.WakeupPressures} 次" +
                    $"（{Percent(stats.WakeupPressureSuccess, stats.WakeupPressures)}）"));

            var reversed = data.Events.Count(e => e.Side == side &&
                e.Kind == "压制被凹" && applied.Any(p =>
                    p.Round == e.Round && p.Start == e.Ordinal));
            if (applied.Length > 0 && reversed * 2 > applied.Length)
                tips.Add(new(player, "防凹",
                    "被凹段数超过自身压制次数的 50%，需要注意防凹。",
                    $"{reversed}/{applied.Length} 段（{Percent(reversed, applied.Length)}）；" +
                    $"至少 {minPressureMoves} 招"));

            if (stats.ChargedMixupsKnown)
            {
                var wrongCharged = stats.ChargedMixupsWrongGuard;
                if (wrongCharged > 2)
                    tips.Add(new(player, "蓄力择",
                        "蓄力择错防超过 2 次，需要加强蓄力择反应。",
                        $"确认防御接触中错防 {wrongCharged} 次；防对 {stats.ChargedMixupsBlocked} 次；" +
                        "蓄力攻击命中不计为错防"));
            }
            else tips.Add(new(player, "蓄力择", "数据不足，无法评价蓄力择错防。",
                "当前轨迹缺少蓄力动作原生字段"));

            if (stats.GuardLevelKnown)
            {
                var decisions = stats.StandGuards + stats.CrouchGuards;
                if (decisions > 0 && stats.WrongGuards * 5 > decisions * 2)
                    tips.Add(new(player, "上下择防守",
                        "对有上下择的攻击错防率超过 40%，需要加强对对方机体压制套路的理解。",
                        $"{stats.WrongGuards}/{decisions} 次" +
                        $"（{Percent(stats.WrongGuards, decisions)}）；仅计明确上下段体术的确认防御接触"));
            }
            else tips.Add(new(player, "上下择防守", "数据不足，无法评价上下择错防率。",
                "当前轨迹缺少攻击上下段原生字段"));

            AddSingleStatTips(player, stats, tips);
            AddMoveTips(data, side, player, actionCounts, moveCounts, tips);
        }
        return tips;
    }

    private static void AddSingleStatTips(string player, SideStats stats,
        List<InterpretationTip> tips)
    {
        if (stats.Inputs >= 50 && stats.RedundantInputs * 5 >= stats.Inputs * 2)
            tips.Add(new(player, "输入复查",
                "较多按键在观察窗口内没有对应动作，建议回看输入节奏与重复按键。",
                $"未对应动作 {stats.RedundantInputs}/{stats.Inputs} 次" +
                $"（{Percent(stats.RedundantInputs, stats.Inputs)}）；至少 50 次按键，达到 40% 提示"));

        if (stats.DdAttempts >= 5 && stats.DdSuccess * 2 < stats.DdAttempts)
            tips.Add(new(player, "DD 输入",
                "已识别的 DD 尝试中，进入对应动作的比例低于一半；建议回看方向与两次 D 的间隔。",
                $"成功 {stats.DdSuccess}/{stats.DdAttempts} 次" +
                $"（{Percent(stats.DdSuccess, stats.DdAttempts)}）；至少 5 次尝试"));

        if (stats.EmptySpirit >= 2)
            tips.Add(new(player, "灵力管理",
                "本局灵力多次降至零，建议回看这些时段的防御与资源使用。",
                $"灵力由正值降至零 {stats.EmptySpirit} 次"));
        else if (stats.CombatFrames >= 600 &&
            stats.LowSpiritFrames * 5 >= stats.CombatFrames)
            tips.Add(new(player, "灵力管理",
                "灵力低于 400 的时间较长，建议回看资源消耗集中的时段。",
                $"低灵力 {stats.LowSpiritFrames}/{stats.CombatFrames} 帧" +
                $"（{Percent(stats.LowSpiritFrames, stats.CombatFrames)}）；至少 600 对战帧，达到 20% 提示"));

        if (stats.Techs >= 3 && stats.TechTrapped >= 3)
            tips.Add(new(player, "受身复查",
                "多次受身后尚未恢复自由行动便再次受击，建议回看受身方向和时机。",
                $"受身未自由行动被打 {stats.TechTrapped}/{stats.Techs} 次"));

        if (stats.PunishPhaseKnown && stats.StartupPunished >= 3)
            tips.Add(new(player, "出招前摇",
                "多次在出招前摇阶段被确认命中，建议回看发动时的距离与对方行动。",
                $"前摇被打 {stats.StartupPunished} 次；原生动作和近战框字段可用"));
        if (stats.PunishPhaseKnown && stats.RecoveryPunished >= 3)
            tips.Add(new(player, "出招硬直",
                "多次在出招后段硬直被确认命中，建议回看落空后的站位与取消选择。",
                $"硬直被打 {stats.RecoveryPunished} 次；原生动作和近战框字段可用"));

        if (stats.ProjectileOpenerKnown && stats.TakenHits >= 5 &&
            stats.TakenProjectileOpeners * 2 >= stats.TakenHits)
            tips.Add(new(player, "弹幕起手防守",
                "被确认命中的起手中，弹幕起手占比较高；建议回看接近路线和防御时机。",
                $"被弹幕起手 {stats.TakenProjectileOpeners}/{stats.TakenHits} 次" +
                $"（{Percent(stats.TakenProjectileOpeners, stats.TakenHits)}）；至少 5 次受击"));

    }

    public static IReadOnlyDictionary<(int Side, int Round, string Move), int> MoveCounts(
        ReplaySession data) => MoveCounts(data, ActionCounts(data));

    private static Dictionary<(int Side, int Round, string Move), int> MoveCounts(
        ReplaySession data, IReadOnlyDictionary<(int Side, int Round, int Action), int> actions)
    {
        var result = new Dictionary<(int Side, int Round, string Move), int>();
        var charactersByRound = new Dictionary<int, (int P1, int P2)>();
        foreach (var frame in data.Frames)
            if (frame.MatchState == 2 && !charactersByRound.ContainsKey(frame.Round))
                charactersByRound[frame.Round] = (frame.P1.Character, frame.P2.Character);
        foreach (var ((side, round, action), count) in actions)
        {
            if (!charactersByRound.TryGetValue(round, out var characters)) continue;
            var character = side == 0 ? characters.P1 : characters.P2;
            var key = (side, round, ActionCatalog.Group(action, character));
            result[key] = result.GetValueOrDefault(key) + count;
        }
        return result;
    }

    private static Dictionary<(int Side, int Round, int Action), int> ActionCounts(
        ReplaySession data)
    {
        var counts = new Dictionary<(int Side, int Round, int Action), int>();
        Frame? previous = null;
        foreach (var frame in data.Frames)
        {
            if (frame.MatchState != 2 || frame.Round < 0) { previous = frame; continue; }
            for (var side = 0; side < 2; side++)
            {
                var fighter = frame.Side(side);
                if (fighter.Action is not (>= 300 and < 340 or >= 400 and < 440 or
                    >= 500 and < 594 or >= 600 and < 620)) continue;
                var old = previous?.Side(side);
                if (previous?.MatchState == 2 && previous.Round == frame.Round &&
                    old is not null && old.Action == fighter.Action &&
                    fighter.ActionFrame >= old.ActionFrame) continue;
                var key = (side, frame.Round, fighter.Action);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            previous = frame;
        }
        return counts;
    }

    private static void AddMoveTips(ReplaySession data, int side, string player,
        IReadOnlyDictionary<(int Side, int Round, int Action), int> actions,
        IReadOnlyDictionary<(int Side, int Round, string Move), int> moves,
        List<InterpretationTip> tips)
    {
        var totals = moves.Where(x => x.Key.Side == side)
            .GroupBy(x => x.Key.Move)
            .Select(x => (Name: x.Key, Count: x.Sum(y => y.Value)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
        var total = totals.Sum(x => x.Count);
        if (total > 0)
        {
            var top = totals.Take(3).ToArray();
            tips.Add(new(player, "招式使用率前三",
                string.Join("；", top.Select((x, index) =>
                    $"第 {index + 1} 名 {x.Name} {x.Count} 次（{Percent(x.Count, total)}）")),
                $"已记录攻击动作共 {total} 次；同一技能的 B/C 变体合并"));
            for (var i = 0; i + 1 < top.Length; i++)
                if (top[i + 1].Count > 0 && top[i].Count >= top[i + 1].Count * 8)
                    tips.Add(new(player, "厨招",
                        $"{top[i].Name} 与相邻第 {i + 2} 名次数相差至少 8 倍，定义为厨招。",
                        $"{top[i].Count}/{top[i + 1].Count} 次（{top[i].Count * 1.0 / top[i + 1].Count:0.0} 倍）"));
        }

        var character = side == 0 ? data.Header?.Character1 : data.Header?.Character2;
        if (character is null) return;
        var used = totals.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var unused = CommonActions.Select(x => ActionCatalog.Group(x, character.Value))
            .Distinct(StringComparer.Ordinal).Where(x => !used.Contains(x)).ToList();
        var usedSkillSlots = actions.Where(x => x.Key.Side == side &&
                x.Key.Action is >= 500 and < 594)
            .Select(x => (x.Key.Action - 500) / 20).ToHashSet();
        unused.AddRange(ActionReference.Skills.Where(x => x.Key.Character == character.Value &&
                !usedSkillSlots.Contains(x.Key.Slot))
            .OrderBy(x => x.Key.Slot).Select(x => x.Value.Input + " 系技能"));
        foreach (var chunk in unused.Distinct(StringComparer.Ordinal).Chunk(6))
            tips.Add(new(player, "招式覆盖",
                "本局未使用：" + string.Join("、", chunk) + "；招式使用不全面。",
                "当前角色已收录的常用动作与技能，本局使用次数为 0；未收录动作不评价"));
    }

    private static string Percent(int numerator, int denominator) =>
        (100.0 * numerator / denominator).ToString("0.0") + "%";
}
