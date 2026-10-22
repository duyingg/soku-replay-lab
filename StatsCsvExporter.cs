using System.Globalization;
using System.IO;
using System.Text;

namespace SokuReplayLab;

public static class StatsCsvExporter
{
    private static readonly (string Name, Func<SideStats, string> Value)[] Columns = [
        ("对战秒数", x => (x.CombatFrames * ReplayTiming.SecondsPerFrame).ToString("0.000", CultureInfo.InvariantCulture)),
        ("造成总损血", x => N(x.Damage)),
        ("确认命中伤害", x => N(x.AttackDamage)),
        ("磨血", x => N(x.ChipDamage)),
        ("天气伤害", x => N(x.WeatherDamage)),
        ("其中台风", x => N(x.TyphoonDamage)),
        ("承受总损血", x => N(x.TakenDamage)),
        ("承受确认命中伤害", x => N(x.TakenAttackDamage)),
        ("承受磨血", x => N(x.TakenChipDamage)),
        ("承受天气伤害", x => N(x.TakenWeatherDamage)),
        ("确认连段", x => N(x.Hits)),
        ("承受连段", x => N(x.TakenHits)),
        ("低伤害连段", x => N(x.LowDamageHits)),
        ("低伤害弹幕命中", x => Known(x.ProjectileLowDamageKnown, x.ProjectileLowDamageHits)),
        ("低伤害符卡参与", x => N(x.LowDamageSpellHits)),
        ("极限体术命中", x => Known(x.ExtremeMeleeKnown, x.ExtremeMeleeHits)),
        ("防御接触", x => N(x.Blocks)),
        ("防御硬直帧", x => N(x.GuardFrames)),
        ("平均防御秒数", x => x.DefenseEpisodes == 0 ? "数据不足" :
            (x.GuardFrames * ReplayTiming.SecondsPerFrame / x.DefenseEpisodes).ToString("0.000", CultureInfo.InvariantCulture)),
        ("压制段数", _ => ""),
        ("平均压制秒数", _ => ""),
        ("站防", x => Known(x.GuardLevelKnown, x.StandGuards)),
        ("蹲防", x => Known(x.GuardLevelKnown, x.CrouchGuards)),
        ("错防", x => Known(x.GuardLevelKnown, x.WrongGuards)),
        ("蓄力择错防", x => Known(x.ChargedMixupsKnown, x.ChargedMixupsWrongGuard)),
        ("受身", x => N(x.Techs)),
        ("受身未自由行动被打", x => N(x.TechTrapped)),
        ("抓受身", x => N(x.TechChases)),
        ("抓受身成功", x => N(x.TechChaseSuccess)),
        ("被弹幕起手", x => Known(x.ProjectileOpenerKnown, x.TakenProjectileOpeners)),
        ("同向被攻击", x => N(x.SameDirectionHits)),
        ("前摇被攻击", x => Known(x.PunishPhaseKnown, x.StartupPunished)),
        ("硬直被攻击", x => Known(x.PunishPhaseKnown, x.RecoveryPunished)),
        ("被凹", x => Known(x.PunishPhaseKnown, x.ReversalPunished)),
        ("凹尝试", x => Known(x.ReversalsKnown, x.ReversalsUsed)),
        ("凹命中", x => Known(x.ReversalsKnown, x.ReversalsHit)),
        ("落地取消", x => Known(x.LandingCancelsKnown, x.LandingCancels)),
        ("压起身", x => N(x.WakeupPressures)),
        ("压起身成功", x => N(x.WakeupPressureSuccess)),
        ("压起身被打断", x => N(x.WakeupPressureInterrupted)),
        ("压制抓乱动", x => N(x.PressureMashCatches)),
        ("压制抓DD", x => N(x.PressureDdCatches)),
        ("逃压尝试", x => N(x.EscapeAttempts)),
        ("逃压成功", x => N(x.EscapeSuccess)),
        ("逃压失败", x => N(x.EscapeFailed)),
        ("逃压待判定", x => N(x.EscapeUnresolved)),
        ("按键沿", x => N(x.Inputs)),
        ("有效输入", x => N(x.EffectiveInputs)),
        ("多余输入", x => N(x.RedundantInputs)),
        ("前冲", x => N(x.ForwardDashes)),
        ("后冲", x => N(x.BackDashes)),
        ("DD尝试", x => N(x.DdAttempts)),
        ("DD成功", x => N(x.DdSuccess)),
        ("用卡", x => N(x.CardsUsed)),
        ("破灵珠", x => N(x.BrokenOrbs))
    ];

    public static int Export(string path, ReplaySession session, int side = -1, int round = -1,
        int minPressureMoves = 1)
        => ExportMany(path, [session], side, round, minPressureMoves);

    public static int ExportMany(string path, IEnumerable<ReplaySession> sessions,
        int side = -1, int round = -1, int minPressureMoves = 1)
    {
        if (minPressureMoves is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(minPressureMoves));
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(",", new[] { "REP", "角色", "回合" }
            .Concat(Columns.Select(x => x.Name)).Select(Cell)));
        var count = 0;
        foreach (var session in sessions)
            count += WriteRows(writer, session, side, round, minPressureMoves);
        return count;
    }

    private static int WriteRows(StreamWriter writer, ReplaySession session, int side,
        int round, int minPressureMoves)
    {
        var rows = session.Stats.Where(x => (side < 0 || x.Side == side) &&
            (round < 0 || x.Round == round)).OrderBy(x => x.Round).ThenBy(x => x.Side)
            .ToList();
        if (round < 0)
            for (var player = 0; player < 2; player++)
                if ((side < 0 || side == player) && session.Stats.Any(x => x.Side == player))
                    rows.Add(session.Total(player));
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", new[] {
                Path.GetFileName(session.Source), row.Side == 0 ? "1P" : "2P",
                row.Round < 0 ? "总计" : (row.Round + 1).ToString(CultureInfo.InvariantCulture)
            }.Concat(Columns.Select(x => ColumnValue(session, row, x, minPressureMoves)))
                .Select(Cell)));
        }
        return rows.Count;
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Known(bool known, int value) => known ? N(value) : "数据不足";
    private static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string ColumnValue(ReplaySession session, SideStats row,
        (string Name, Func<SideStats, string> Value) column, int minPressureMoves)
    {
        if (column.Name is "压制段数" or "平均压制秒数")
        {
            var pressures = session.Pressures.Where(p => p.Attacker == row.Side &&
                p.ConfirmedMoves >= minPressureMoves &&
                (row.Round < 0 || p.Round == row.Round)).ToArray();
            return column.Name == "压制段数" ? N(pressures.Length) :
                pressures.Length == 0 ? "数据不足" :
                (pressures.Average(p => p.End - p.Start + 1) * ReplayTiming.SecondsPerFrame)
                    .ToString("0.000", CultureInfo.InvariantCulture);
        }
        if (column.Name.StartsWith("逃压", StringComparison.Ordinal))
        {
            var pressureStarts = session.Pressures.Where(p =>
                p.Defender == row.Side && p.ConfirmedMoves >= minPressureMoves &&
                (row.Round < 0 || p.Round == row.Round))
                .Select(p => (p.Round, p.Start)).ToHashSet();
            var attempts = session.EscapeAttempts.Where(a => a.Defender == row.Side &&
                (row.Round < 0 || a.Round == row.Round) &&
                pressureStarts.Contains((a.Round, a.PressureStart)));
            return N(column.Name switch {
                "逃压尝试" => attempts.Count(),
                "逃压成功" => attempts.Count(a => a.Outcome == "成功"),
                "逃压失败" => attempts.Count(a => a.Outcome == "失败"),
                _ => attempts.Count(a => a.Outcome == "待判定")
            });
        }
        if (column.Name is "压制抓乱动" or "压制抓DD")
        {
            var eligible = session.Pressures.Where(p => p.Attacker == row.Side &&
                p.ConfirmedMoves >= minPressureMoves &&
                (row.Round < 0 || p.Round == row.Round))
                .Select(p => (p.Round, p.Start)).ToHashSet();
            return N(session.Events.Count(e => e.Side == row.Side &&
                e.Kind == column.Name && eligible.Contains((e.Round, e.Ordinal))));
        }
        return column.Value(row);
    }
}
