using System.IO;
using System.Text.Json;

namespace SokuReplayLab;

internal static class ReplayInterpretationRuleCheck
{
    internal static void Run(string reportPath)
    {
        var passed = new List<string>();
        void Check(string name, bool value)
        {
            if (!value) throw new InvalidDataException("REP 解读规则边界失败：" + name);
            passed.Add(name);
        }
        bool Has(ReplaySession session, string category, int minimum = 1) =>
            ReplayInterpretation.Evaluate(session, minimum)
                .Any(x => x.Category == category);
        SideStats Stats() => new() { Side = 0, Round = 0,
            CounterKnown = true, ChargedMixupsKnown = true, GuardLevelKnown = true };

        var counter = Stats();
        counter.TakenHits = 5; counter.TakenCounterHits = 3;
        Check("被康等于60%无效", Has(Make(counter), "对局有效性"));
        counter.TakenCounterHits = 2;
        Check("被康低于60%有效", !Has(Make(counter), "对局有效性"));
        counter.TakenCounterHits = 3; counter.CounterKnown = false;
        Check("缺康特字段不判无效", !Has(Make(counter), "对局有效性"));

        var combos = Enumerable.Range(0, 5).Select(i => Combo(i, i < 2)).ToArray();
        Check("连段40%提示", Has(Make(combos: combos), "连段练习"));
        combos = Enumerable.Range(0, 5).Select(i => Combo(i, i < 3)).ToArray();
        Check("连段等于60%不提示", !Has(Make(combos: combos), "连段练习"));

        var pressures = Enumerable.Range(0, 10).Select(i =>
            Pressure(i, i < 9 ? "脱离" : "命中")).ToArray();
        Check("压制等于90%提示", Has(Make(pressures: pressures), "压制"));
        pressures = Enumerable.Range(0, 10).Select(i =>
            Pressure(i, i < 8 ? "后续未接" : "命中")).ToArray();
        Check("压制低于90%不提示", !Has(Make(pressures: pressures), "压制"));

        pressures = Enumerable.Range(0, 10).Select(i =>
            Pressure(i, i < 8 ? "抓dd" : "命中")).ToArray();
        Check("被抓等于80%提示", ReplayInterpretation.Evaluate(
            Make(pressures: pressures), 1).Any(x => x.Player == "2P" && x.Category == "逃压"));
        pressures = Enumerable.Range(0, 10).Select(i =>
            Pressure(i, i < 7 ? "抓乱动" : "命中")).ToArray();
        Check("被抓低于80%不提示", !Has(Make(pressures: pressures), "逃压"));

        var wakeup = Stats();
        wakeup.WakeupPressures = 5; wakeup.WakeupPressureSuccess = 3;
        Check("压起身60%提示", Has(Make(wakeup), "压起身"));
        wakeup.WakeupPressureSuccess = 4;
        Check("压起身等于80%不提示", !Has(Make(wakeup), "压起身"));

        pressures = Enumerable.Range(0, 2).Select(i => Pressure(i, "被反击")).ToArray();
        Check("被凹等于50%不提示", !Has(Make(pressures: pressures,
            events: [Reversal(0)]), "防凹"));
        pressures = Enumerable.Range(0, 3).Select(i => Pressure(i, "被反击")).ToArray();
        Check("被凹超过50%提示", Has(Make(pressures: pressures,
            events: [Reversal(0), Reversal(1)]), "防凹"));

        var charged = Stats();
        charged.ChargedMixupsFaced = 5; charged.ChargedMixupsBlocked = 2;
        charged.ChargedMixupsWrongGuard = 3;
        Check("蓄力错防3次提示", Has(Make(charged), "蓄力择"));
        charged.ChargedMixupsWrongGuard = 2;
        Check("蓄力错防2次不提示", !Has(Make(charged), "蓄力择"));
        charged.ChargedMixupsWrongGuard = 0; charged.ChargedMixupsFaced = 5;
        charged.ChargedMixupsBlocked = 2;
        Check("蓄力命中不算错防", !Has(Make(charged), "蓄力择"));

        var guards = Stats();
        guards.StandGuards = 3; guards.CrouchGuards = 2; guards.WrongGuards = 2;
        Check("错防等于40%不提示", !Has(Make(guards), "上下择防守"));
        guards.WrongGuards = 3;
        Check("错防超过40%提示", Has(Make(guards), "上下择防守"));

        var frames = Frames([300, 300, 300, 300, 300, 300, 300, 300, 302, 303]);
        var moveTips = ReplayInterpretation.Evaluate(Make(frames: frames), 1);
        Check("招式前三及8倍厨招", moveTips.Any(x => x.Player == "1P" &&
            x.Category == "招式使用率前三") && moveTips.Any(x =>
            x.Player == "1P" && x.Category == "厨招" &&
            x.Evidence.Contains("8.0 倍", StringComparison.Ordinal)));
        Check("零次常用招式提示", moveTips.Any(x => x.Player == "1P" &&
            x.Category == "招式覆盖" && x.Observation.Contains("4a", StringComparison.Ordinal)));

        var input = Stats();
        input.Inputs = 50; input.RedundantInputs = 20;
        Check("未对应动作等于40%提示", Has(Make(input), "输入复查"));
        input.RedundantInputs = 19;
        Check("未对应动作低于40%不提示", !Has(Make(input), "输入复查"));
        input.Inputs = 49; input.RedundantInputs = 25;
        Check("输入样本不足不提示", !Has(Make(input), "输入复查"));

        var dd = Stats();
        dd.DdAttempts = 5; dd.DdSuccess = 2;
        Check("DD成功低于50%提示", Has(Make(dd), "DD 输入"));
        dd.DdSuccess = 3;
        Check("DD成功过半不提示", !Has(Make(dd), "DD 输入"));
        dd.DdAttempts = 4; dd.DdSuccess = 1;
        Check("DD样本不足不提示", !Has(Make(dd), "DD 输入"));

        var spirit = Stats();
        spirit.CombatFrames = 600; spirit.LowSpiritFrames = 120;
        Check("低灵力等于20%提示", Has(Make(spirit), "灵力管理"));
        spirit.CombatFrames = 599;
        Check("对战帧不足不提示低灵力", !Has(Make(spirit), "灵力管理"));
        spirit.CombatFrames = 600;
        spirit.EmptySpirit = 2;
        Check("灵力归零优先且只报一条", ReplayInterpretation.Evaluate(Make(spirit), 1)
            .Count(x => x.Player == "1P" && x.Category == "灵力管理") == 1);

        var tech = Stats();
        tech.Techs = 8; tech.TechTrapped = 3;
        Check("受身后被打三次提示", Has(Make(tech), "受身复查"));
        tech.TechTrapped = 2;
        Check("受身后被打两次不提示", !Has(Make(tech), "受身复查"));

        var startup = Stats();
        startup.StartupPunished = 3;
        Check("缺原生前摇字段不提示", !Has(Make(startup), "出招前摇"));
        startup.PunishPhaseKnown = true;
        Check("前摇被打三次提示", Has(Make(startup), "出招前摇"));
        startup.RecoveryPunished = 3;
        Check("后段硬直被打三次提示", Has(Make(startup), "出招硬直"));

        var projectile = Stats();
        projectile.TakenHits = 6; projectile.TakenProjectileOpeners = 3;
        Check("缺弹幕框字段不提示", !Has(Make(projectile), "弹幕起手防守"));
        projectile.ProjectileOpenerKnown = true;
        Check("弹幕起手等于50%提示", Has(Make(projectile), "弹幕起手防守"));
        projectile.TakenProjectileOpeners = 2;
        Check("弹幕起手低于50%不提示", !Has(Make(projectile), "弹幕起手防守"));

        File.WriteAllText(reportPath, JsonSerializer.Serialize(new {
            passed = passed.Count, cases = passed
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ReplaySession Make(SideStats? first = null,
        IReadOnlyList<Combo>? combos = null, IReadOnlyList<Pressure>? pressures = null,
        IReadOnlyList<ReplayEvent>? events = null, IReadOnlyList<Frame>? frames = null)
    {
        var fighter = Fighter(0);
        return new ReplaySession {
            Source = "rule-fixture.rep",
            Header = new ReplayHeader("rule-fixture.rep", "fixture", "1P", "2P", 0, 0, 0),
            Frames = frames ?? [new Frame(0, 0, 2, 0, 0, fighter, fighter)],
            Events = events ?? [], Combos = combos ?? [], Pressures = pressures ?? [],
            EscapeAttempts = [], PressureGaps = [], Curves = [], CardEvents = [],
            Stats = [first ?? new SideStats { Side = 0, Round = 0,
                CounterKnown = true, ChargedMixupsKnown = true, GuardLevelKnown = true },
                new SideStats { Side = 1, Round = 0,
                    CounterKnown = true, ChargedMixupsKnown = true, GuardLevelKnown = true }]
        };
    }

    private static Fighter Fighter(int action) => new(0, 0, 0, action, 0,
        10000, 1000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static Combo Combo(int id, bool finished) => new(id, id, 0, 0,
        1, 100, 0, "", "", finished, false, []);
    private static Pressure Pressure(int id, string end) => new(id, id + 1,
        0, 0, 1, 1, 1, "", end);
    private static ReplayEvent Reversal(int id) => new(id, 0, 0,
        "压制被凹", "", "");
    private static IReadOnlyList<Frame> Frames(IReadOnlyList<int> actions)
    {
        var result = new List<Frame>();
        var opponent = Fighter(0);
        foreach (var action in actions)
        {
            result.Add(new Frame(result.Count, result.Count, 2, 0, 0,
                Fighter(action), opponent));
            result.Add(new Frame(result.Count, result.Count, 2, 0, 0,
                Fighter(0), opponent));
        }
        return result;
    }
}
