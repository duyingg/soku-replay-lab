using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SokuReplayLab;

public sealed partial class MainWindow
{
    // Keep the old program's per-round catalogue and category order. Values
    // come from the current C# analyzer; an unavailable observation stays blank.
    private UIElement LegacyStatsSideReport(ReplaySession data, int side)
    {
        var rounds = data.Stats.Where(x => x.Side == side &&
            (statsRound < 0 || x.Round == statsRound))
            .OrderBy(x => x.Round).ToArray();
        if (rounds.Length == 0) return Text("该角色没有有效逐本统计。", 12, Muted);
        var columns = new[] { "统计项" }.Concat(rounds.Select(x => $"第 {x.Round + 1} 本"))
            .Append("每本平均").ToArray();
        var rows = new List<string[]>();
        if (statsCategory == "招式使用")
        {
            foreach (var move in MoveUsage(data).Where(x => x.Side == side &&
                (statsRound < 0 || x.Round == statsRound))
                .GroupBy(x => x.Move).OrderByDescending(x => x.Sum(y => y.Count)))
            {
                var counts = rounds.Select(round =>
                    move.Where(x => x.Round == round.Round).Sum(x => x.Count)).ToArray();
                rows.Add([move.Key, .. counts.Select(x => x.ToString()),
                    counts.Average().ToString("0.0")]);
            }
        }
        else
        {
            void Group(string name)
            {
                if (statsCategory == "全表")
                    rows.Add(["▸ " + name, .. Enumerable.Repeat("", rounds.Length + 1)]);
            }
            void Add(string group, string label, Func<SideStats, double?> value, int digits = 0)
            {
                if (statsCategory != "全表" && statsCategory != group) return;
                var values = rounds.Select(value).ToArray();
                static string Display(double? number, int precision) => number is null
                    ? "数据不足" : number.Value.ToString($"N{precision}");
                var known = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                rows.Add([label, .. values.Select(x => Display(x, digits)),
                    Display(known.Length == 0 ? null : known.Average(), Math.Max(1, digits))]);
            }
            double PressureSeconds(SideStats x) => data.Pressures.Where(p =>
                p.Attacker == side && p.Round == x.Round &&
                p.ConfirmedMoves >= minPressureMoves)
                .Sum(p => (p.End - p.Start + 1) * ReplayTiming.SecondsPerFrame);
            int PressureCount(SideStats x) => data.Pressures.Count(p =>
                p.Attacker == side && p.Round == x.Round &&
                p.ConfirmedMoves >= minPressureMoves);
            double DefenseSeconds(SideStats x) => data.Pressures.Where(p =>
                p.Defender == side && p.Round == x.Round &&
                p.ConfirmedMoves >= minPressureMoves)
                .Sum(p => (p.End - p.Start + 1) * ReplayTiming.SecondsPerFrame);
            int DefenseCount(SideStats x) => data.Pressures.Count(p =>
                p.Defender == side && p.Round == x.Round &&
                p.ConfirmedMoves >= minPressureMoves);

            Group("时间、资源与输入");
            Add("时间、资源与输入", "对局时长（秒）", x => x.Seconds, 1);
            Add("时间、资源与输入", "总压制时长（秒）", x => PressureSeconds(x), 1);
            Add("时间、资源与输入", "平均每次压制时长（秒）", x =>
                PressureCount(x) > 0 ? PressureSeconds(x) / PressureCount(x) : null, 1);
            Add("时间、资源与输入", "总防御时长（秒）", x => DefenseSeconds(x), 1);
            Add("时间、资源与输入", "平均每次防御时长（秒）", x =>
                DefenseCount(x) > 0 ? DefenseSeconds(x) / DefenseCount(x) : null, 1);
            Add("时间、资源与输入", "空灵次数", x => x.EmptySpirit);
            Add("时间、资源与输入", "低灵力总时长（秒）", x => x.LowSpiritFrames * ReplayTiming.SecondsPerFrame, 1);
            Add("时间、资源与输入", "最长低灵力（秒）", x => x.LongestLowSpiritFrames * ReplayTiming.SecondsPerFrame, 1);
            Add("时间、资源与输入", "总按键次数", x => x.Inputs);
            Add("时间、资源与输入", "有效按键次数", x => x.EffectiveInputs);
            Add("时间、资源与输入", "多余按键次数", x => x.RedundantInputs);
            Add("时间、资源与输入", "平均每秒有效按键", x =>
                x.Seconds > 0 ? x.EffectiveInputs / x.Seconds : null, 1);
            Add("时间、资源与输入", "平均每秒移动距离", x =>
                x.Seconds > 0 ? x.Distance / 1280 / x.Seconds : null, 2);

            Group("命中与伤害");
            Add("命中与伤害", "命中次数（连段）", x => x.Hits);
            Add("命中与伤害", "平均每次攻击伤害", x =>
                x.Hits > 0 ? (double)x.AttackDamage / x.Hits : null, 1);
            Add("命中与伤害", "被命中次数（连段）", x => x.TakenHits);
            Add("命中与伤害", "与对手同向时被攻击", x => x.SameDirectionHits);
            Add("命中与伤害", "造成伤害", x => x.Damage);
            Add("命中与伤害", "承受伤害", x => x.TakenDamage);
            Add("命中与伤害", "磨血总量", x => x.ChipDamage);
            Add("命中与伤害", "天气造成伤害", x => x.WeatherDamage);
            Add("命中与伤害", "天气承受伤害", x => x.TakenWeatherDamage);
            Add("命中与伤害", "台风造成伤害", x => x.TyphoonDamage);
            Add("命中与伤害", "台风承受伤害", x => x.TakenTyphoonDamage);

            Group("攻防判定");
            Add("攻防判定", "被凹次数", x => x.PunishPhaseKnown ? x.ReversalPunished : null);
            Add("攻防判定", "康特次数（排除黄砂）", x => x.CounterKnown ? x.CounterHits : null);
            Add("攻防判定", "被康特次数", x => x.CounterKnown ? x.TakenCounterHits : null);
            Add("攻防判定", "被弹幕起手次数", x =>
                x.ProjectileOpenerKnown ? x.TakenProjectileOpeners : null);
            Add("攻防判定", "体术极限距离命中", x =>
                x.ExtremeMeleeKnown ? x.ExtremeMeleeHits : null);
            Add("攻防判定", "体术极限距离被命中", x =>
                x.TakenExtremeMeleeKnown ? x.TakenExtremeMeleeHits : null);
            Add("攻防判定", "出圈或倒地次数", x => x.CircleOrDownHits);
            Add("攻防判定", "命中后未出圈或倒地", x => x.UnfinishedHits);
            Add("攻防判定", "低于 1500 伤害连段", x => x.LowDamageHits);
            Add("攻防判定", "低伤弹幕起手", x =>
                x.ProjectileLowDamageKnown ? x.ProjectileLowDamageHits : null);
            Add("攻防判定", "被低伤弹幕起手", x =>
                x.ProjectileLowDamageKnown ? x.TakenLowDamageHits : null);
            Add("攻防判定", "符卡参与低伤", x => x.LowDamageSpellHits);
            Add("攻防判定", "技能前摇被攻击", x =>
                x.PunishPhaseKnown ? x.StartupPunished : null);
            Add("攻防判定", "技能硬直被攻击", x =>
                x.PunishPhaseKnown ? x.RecoveryPunished : null);

            Group("防御");
            Add("防御", "站防次数", x => x.GuardLevelKnown ? x.StandGuards : null);
            Add("防御", "蹲防次数", x => x.GuardLevelKnown ? x.CrouchGuards : null);
            Add("防御", "错防次数", x => x.GuardLevelKnown ? x.WrongGuards : null);
            Add("防御", "被蓄力择次数", x => x.ChargedMixupsKnown ? x.ChargedMixupsFaced : null);
            Add("防御", "蓄力择防对次数", x => x.ChargedMixupsKnown ? x.ChargedMixupsBlocked : null);
            Add("防御", "破灵珠次数（含 DD）", x => x.BrokenOrbs);
            Add("防御", "被磨血总量", x => x.TakenChipDamage);

            Group("主动压制");
            Add("主动压制", "抓受身次数", x => x.TechChases);
            Add("主动压制", "抓受身成功", x => x.TechChaseSuccess);
            Add("主动压制", "压起身次数", x => x.WakeupPressures);
            Add("主动压制", "压起身成功", x => x.WakeupPressureSuccess);
            Add("主动压制", "压起身被抢", x => x.WakeupPressureInterrupted);
            Add("主动压制", "压起身被凹", x => x.WakeupPressureReversed);
            Add("主动压制", "压制次数", x => PressureCount(x));
            Add("主动压制", "压制中途被抢", x => x.PressureInterrupted);
            Add("主动压制", "压制中途被凹", x => x.PressureReversed);
            Add("主动压制", "压制抓乱动", x => x.PressureMashCatches);
            Add("主动压制", "压制抓 DD", x => x.PressureDdCatches);

            Group("受身与逃压");
            Add("受身与逃压", "受身次数", x => x.Techs);
            Add("受身与逃压", "受身未自由行动被打", x => x.TechTrapped);
            Add("受身与逃压", "被压制次数", x => DefenseCount(x));
            Add("受身与逃压", "被抓乱动次数", x => x.CaughtMashing);
            Add("受身与逃压", "使用凹次数", x => x.ReversalsKnown ? x.ReversalsUsed : null);
            Add("受身与逃压", "凹中次数", x => x.ReversalsKnown ? x.ReversalsHit : null);
            Add("受身与逃压", "DD 尝试", x => x.DdAttempts);
            Add("受身与逃压", "DD 成功", x => x.DdSuccess);
            Add("受身与逃压", "逃压尝试", x => x.EscapeAttempts);
            Add("受身与逃压", "逃压成功", x => x.EscapeSuccess);
            Add("受身与逃压", "逃压失败", x => x.EscapeFailed);
            Add("受身与逃压", "逃压未判定", x => x.EscapeUnresolved);

            Group("移动与招式");
            Add("移动与招式", "66 次数", x => x.ForwardDashes);
            Add("移动与招式", "44 次数", x => x.BackDashes);
            Add("移动与招式", "落地取消次数", x =>
                x.LandingCancelsKnown ? x.LandingCancels : null);
            foreach (var direction in "12346789")
                Add("移动与招式", direction + "D 次数", x =>
                    x.DDirections.GetValueOrDefault(direction.ToString()));
            foreach (var jump in new[] { "27", "28", "29" })
                Add("移动与招式", jump + " 大跳", x => x.HighJumps.GetValueOrDefault(jump));
            foreach (var jump in new[] { "7", "8", "9" })
                Add("移动与招式", jump + " 小跳", x => x.SmallJumps.GetValueOrDefault(jump));
        }
        var table = Table(columns, rows);
        table.LoadingRow += (_, args) => {
            if (args.Row.Item is DataRowView row &&
                row["统计项"]?.ToString()?.StartsWith("▸ ", StringComparison.Ordinal) == true)
            {
                args.Row.FontWeight = FontWeights.SemiBold;
                args.Row.Background = palette.Alternating;
            }
        };
        return table;
    }
}
