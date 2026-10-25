namespace SokuReplayLab;

public static class ActionCatalog
{
    private static readonly Dictionary<int, string> Notation = new()
    {
        [300] = "a", [301] = "f5a", [302] = "6a", [303] = "2a", [304] = "3a",
        [305] = "66a", [306] = "ja", [307] = "j6a", [308] = "j2a", [309] = "j8a",
        [311] = "f2a", [320] = "a2", [321] = "a3", [322] = "a4", [323] = "a5",
        [324] = "a3a", [325] = "ja2", [330] = "4a", [331] = "3a", [332] = "L6a",
        [400] = "5b", [401] = "6b", [402] = "2b", [403] = "3b", [404] = "j5b",
        [405] = "j6b", [406] = "j2b", [407] = "4b", [408] = "66b", [409] = "j4b",
        [410] = "5c", [411] = "6c", [412] = "2c", [413] = "4c", [414] = "j5c",
        [415] = "j6c", [416] = "j2c", [417] = "j1c", [418] = "66c", [419] = "4c",
        [420] = "Orreries b", [422] = "jOrreries b",
        [430] = "Orreries c", [432] = "jOrreries c"
    };

    private static readonly Dictionary<int, string> Common = new()
    {
        [0] = "站立", [1] = "下蹲过程", [2] = "蹲下", [3] = "起身",
        [4] = "前走", [5] = "后走", [6] = "原地跳", [7] = "前跳",
        [8] = "后跳", [9] = "下落", [10] = "落地", [97] = "倒地",
        [73] = "空中受击·直接倒地", [74] = "空中受击·可撞墙",
        [75] = "空中受击·大硬直Ⅳ", [76] = "撞墙", [77] = "空中受击·极大硬直",
        [78] = "撞墙", [88] = "空中受击·将撞地", [89] = "撞地",
        [98] = "静止倒地", [99] = "KO 后倒地", [100] = "被抓取",
        [143] = "地面破防", [145] = "空中破防", [158] = "空中防御",
        [180] = "向前空受身", [181] = "向后空受身", [197] = "前受身",
        [198] = "后受身", [199] = "原地受身", [200] = "前冲",
        [201] = "后撤", [202] = "空中前冲", [203] = "空中后撤",
        [204] = "地冲收招", [206] = "荷叶前冲", [207] = "荷叶后撤",
        [208] = "原地高跳", [209] = "前高跳", [210] = "后高跳",
        [211] = "地冲取消原地高跳", [212] = "地冲取消前高跳",
        [214] = "飞翔", [215] = "硬落地", [218] = "诹访子 j2D",
        [219] = "诹访子 j1D/j3D", [220] = "回避结界 2DD",
        [222] = "回避结界 1DD", [223] = "回避结界 6DD",
        [224] = "回避结界 4DD", [225] = "回避结界 j4DD",
        [226] = "回避结界 j6DD", [690] = "技能卡", [691] = "系统卡",
        [692] = "伊吹瓢", [693] = "回复符", [694] = "魔法药水",
        [695] = "灵击符", [696] = "反击符", [697] = "左扇",
        [698] = "符卡破坏药"
    };

    public static string Display(int action, int character)
    {
        if (Notation.TryGetValue(action, out var notation)) return notation;
        if (Common.TryGetValue(action, out var common)) return common;
        if (character == 1 && action == 504) return "ウィッチレイライン·空振り";
        if (character == 5 && action == 529) return "折伏无间·追击";
        if (character == 11 && action == 529) return "疾走优美·后续";
        if (character == 19)
        {
            if (action is 520 or 522) return "大蛤蟆神·特殊动作";
            if (action is >= 565 and <= 568)
                return $"诅咒的大地·{new[] { "地上B", "地上C", "地中B", "地中C" }[action - 565]}";
        }
        if (action is >= 500 and <= 593)
        {
            var slot = (action - 500) / 20;
            var offset = (action - 500) % 20;
            var variant = offset / 5;
            var button = offset % 5;
            if (slot < 5 && variant < 3 && button < 4 &&
                ActionReference.Skills.TryGetValue((character, slot), out var skill))
            {
                var label = skill.Names[variant] == "原" ? "默认技能" : skill.Names[variant];
                var buttonName = ButtonName(character, action, button);
                var prefix = buttonName.Contains("空中") ? "j" : buttonName.Contains("荷叶") ? "L" : "";
                return $"{prefix}{skill.Input}{char.ToLowerInvariant(buttonName[^1])}{label}";
            }
            return $"技能动作 {action}";
        }
        var card = action switch { >= 600 and <= 619 => action - 400,
            >= 650 and <= 669 => action - 450, _ => -1 };
        if (card >= 0)
            return ActionReference.Spells.TryGetValue((character, card), out var spell)
                ? action >= 650 ? $"{spell}·后续" : spell
                : $"未映射符卡 {card}";
        foreach (var (start, label) in new[] {
            (50, "站立受击"), (56, "站立受击Ⅱ"), (62, "蹲姿受击"),
            (70, "空中受击"), (150, "正确高防"), (154, "正确低防"),
            (159, "错误高防"), (163, "错误低防") })
        {
            var count = start is 56 or 62 or 70 ? 3 : 4;
            if (action >= start && action < start + count)
                return $"{label}·{new[] { "小", "中", "大", "极大" }[action - start]}硬直";
        }
        return $"动作 {action}";
    }

    public static string Route(string raw, int character)
        => string.Join(" → ", raw.Split(" → ").Select(segment =>
            int.TryParse(segment, out var action)
                ? $"{Display(action, character)} [{action}]" : segment));

    public static string RouteCompact(string raw, int character)
        => string.Join(" ", raw.Split(" → ").Select(segment =>
            int.TryParse(segment, out var action)
                ? Display(action, character) : segment));

    public static string Group(int action, int character)
    {
        if (action is >= 500 and <= 593)
        {
            var slot = (action - 500) / 20;
            var offset = (action - 500) % 20;
            var variant = offset / 5;
            if (slot < 5 && variant < 3 && ActionReference.Skills.TryGetValue((character, slot), out var skill))
                return $"{skill.Input}{(skill.Names[variant] == "原" ? "默认技能" : skill.Names[variant])}";
        }
        return Display(action, character);
    }

    private static string ButtonName(int character, int action, int button)
    {
        if (character == 19)
        {
            if (action is >= 500 and <= 503 or >= 540 and <= 553)
                return new[] { "地中B", "地中C", "空中B", "空中C" }[button];
            if (action is >= 505 and <= 508 or >= 510 and <= 513 or >= 560 and <= 563)
                return new[] { "地上B", "地上C", "荷叶B", "荷叶C" }[button];
            if (action is >= 525 and <= 528)
                return new[] { "荷叶B", "荷叶C", "空中B", "空中C" }[button];
            if (action is >= 532 and <= 533)
                return new[] { "空中B", "空中C", "空中B", "空中C" }[button];
        }
        return new[] { "B", "C", "空中B", "空中C" }[button];
    }
}
