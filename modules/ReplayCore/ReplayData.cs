using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SokuReplayLab;

public sealed record ReplayHeader(string Path, string Hash, string Player1, string Player2,
    int InputFrames, int Character1, int Character2)
{
    public string? ProfileName1 { get; init; }
    public string? ProfileName2 { get; init; }
    private static readonly string[] Characters =
    [
        "博丽灵梦", "雾雨魔理沙", "十六夜咲夜", "爱丽丝", "帕秋莉", "魂魄妖梦",
        "蕾米莉亚", "西行寺幽幽子", "八云紫", "伊吹萃香", "铃仙", "射命丸文",
        "小野塚小町", "永江衣玖", "比那名居天子", "东风谷早苗", "琪露诺",
        "红美铃", "灵乌路空", "洩矢诹访子"
    ];

    public static ReplayHeader Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 20 || U32(data, 0) != 0xD2)
            throw new InvalidDataException("不是已验证的 0xD2 REP 格式。");
        var first = U16(data, 14) & 0xff;
        var count1 = checked((int)U32(data, 16));
        if (count1 > 100) throw new InvalidDataException("第一卡组数量异常。");
        var secondOffset = 20 + count1 * 2;
        if (data.Length < secondOffset + 9) throw new InvalidDataException("REP 头不完整。");
        var second = U16(data, secondOffset + 3) & 0xff;
        var count2 = checked((int)U32(data, secondOffset + 5));
        if (count2 > 100) throw new InvalidDataException("第二卡组数量异常。");
        var payloadOffset = secondOffset + 9 + count2 * 2 + 14;
        if (data.Length < payloadOffset) throw new InvalidDataException("REP 头不完整。");
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var (profile1, profile2) = ProfileNamesFromFileName(path);
        return new(path, hash, Name(first), Name(second),
            (data.Length - payloadOffset) / 4, first, second) {
                ProfileName1 = profile1, ProfileName2 = profile2
            };
    }

    private static (string? P1, string? P2) ProfileNamesFromFileName(string path)
    {
        // REP payload has no player-profile-name field. Match the replay filename's
        // profile(character)_profile(character) convention without guessing from
        // the active game's current profiles.
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var match = Regex.Match(name,
            @"^(?:\d{2}|\d{4})-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_(.+?)\([^)]*\)_(.+?)\([^)]*\)(?:_|$)");
        if (!match.Success) return (null, null);
        static string? Clean(string value)
        {
            var profileName = value.Trim();
            return profileName.Length is > 0 and <= 64 ? profileName : null;
        }
        return (Clean(match.Groups[1].Value), Clean(match.Groups[2].Value));
    }

    private static string Name(int id) => id < Characters.Length ? Characters[id] : $"角色 {id}";
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
}

public sealed record Fighter(int Character, double X, double Y, int Action, int ActionFrame,
    int Hp, int Spirit, int Input, int ComboCount, int ComboDamage, int ComboLimit,
    int CardCount, int CardGauge, int HitStop, int Collision, int DamageLimited,
    int FrameFlags, int[]? HandIds = null, int[]? UsedIds = null,
    int Direction = -1, int MaxSpirit = -1, int BrokenOrb = -1,
    int CardSelected = -1, int ChargedAttack = -1, int AttackFlags = -1,
    int CounterVulnerable = -1, int Untech = -1, int ActiveMeleeReach = -1,
    int MeleeMaxReach = -1);
public sealed record Frame(int Ordinal, int EngineFrame, int MatchState, int Round,
    int Weather, Fighter P1, Fighter P2)
{
    public Fighter Side(int side) => side == 0 ? P1 : P2;
}
public sealed record ReplayEvent(int Ordinal, int Round, int Side, string Kind, string Detail,
    string Reason)
{
    public double Second => Ordinal * ReplayTiming.SecondsPerFrame;
}
public sealed record Combo(int Start, int End, int Round, int Side, int Hits,
    int Damage, int Limit, string Route, string Position, bool Circle, bool Knockdown,
    IReadOnlyList<int> Impacts)
{
    public double Second => Start * ReplayTiming.SecondsPerFrame;
}
public sealed record Pressure(int Start, int End, int Round, int Attacker,
    int Defender, int Contacts, int ConfirmedMoves, string Route, string EndReason)
{
    public double Second => Start * ReplayTiming.SecondsPerFrame;
}
public sealed record Curve(int Ordinal, int Round, int P1Hp, int P2Hp,
    int P1Spirit, int P2Spirit, double P1Cards, double P2Cards)
{
    public double Second => Ordinal * ReplayTiming.SecondsPerFrame;
}
public sealed class SideStats
{
    public int Side { get; init; }
    public int Round { get; init; }
    public int CombatFrames { get; set; }
    public int Damage { get; set; }
    public int TakenDamage { get; set; }
    public int ChipDamage { get; set; }
    public int TakenChipDamage { get; set; }
    public int WeatherDamage { get; set; }
    public int TakenWeatherDamage { get; set; }
    public int TyphoonDamage { get; set; }
    public int TakenTyphoonDamage { get; set; }
    public int AttackDamage { get; set; }
    public int TakenAttackDamage { get; set; }
    public int Inputs { get; set; }
    public int EffectiveInputs { get; set; }
    public int RedundantInputs { get; set; }
    public int ForwardDashes { get; set; }
    public int BackDashes { get; set; }
    public int DdAttempts { get; set; }
    public int DdSuccess { get; set; }
    public Dictionary<string, int> DDirections { get; } = [];
    public Dictionary<string, int> HighJumps { get; } = [];
    public Dictionary<string, int> SmallJumps { get; } = [];
    public int Hits { get; set; }
    public int TakenHits { get; set; }
    public int Blocks { get; set; }
    public int Pressures { get; set; }
    public int CardsUsed { get; set; }
    public int GuardFrames { get; set; }
    public int DefenseEpisodes { get; set; }
    public int StandGuards { get; set; }
    public int CrouchGuards { get; set; }
    public int WrongGuards { get; set; }
    public bool GuardLevelKnown { get; set; }
    public int ChargedMixupsFaced { get; set; }
    public int ChargedMixupsBlocked { get; set; }
    public int ChargedMixupsWrongGuard { get; set; }
    public bool ChargedMixupsKnown { get; set; }
    public int LowSpiritFrames { get; set; }
    public int LongestLowSpiritFrames { get; set; }
    public int EmptySpirit { get; set; }
    public int BrokenOrbs { get; set; }
    public int Techs { get; set; }
    public int TechTrapped { get; set; }
    public int TechChases { get; set; }
    public int TechChaseSuccess { get; set; }
    public int ReversalsUsed { get; set; }
    public int ReversalsHit { get; set; }
    public bool ReversalsKnown { get; set; }
    public int TakenProjectileOpeners { get; set; }
    public bool ProjectileOpenerKnown { get; set; }
    public int SameDirectionHits { get; set; }
    public int StartupPunished { get; set; }
    public int RecoveryPunished { get; set; }
    public int ReversalPunished { get; set; }
    public bool PunishPhaseKnown { get; set; }
    public int LandingCancels { get; set; }
    public bool LandingCancelsKnown { get; set; }
    public int LimitBreaks { get; set; }
    public int LowDamageHits { get; set; }
    public int ProjectileLowDamageHits { get; set; }
    public int TakenLowDamageHits { get; set; }
    public bool ProjectileLowDamageKnown { get; set; }
    public int LowDamageSpellHits { get; set; }
    public int LowDamageSpellOnlyHits { get; set; }
    public int ExtremeMeleeHits { get; set; }
    public int TakenExtremeMeleeHits { get; set; }
    public bool ExtremeMeleeKnown { get; set; } = true;
    public bool TakenExtremeMeleeKnown { get; set; } = true;
    public int WakeupPressureFailures { get; set; }
    public int WakeupPressures { get; set; }
    public int WakeupPressureSuccess { get; set; }
    public int WakeupPressureInterrupted { get; set; }
    public int WakeupPressureReversed { get; set; }
    public int PressureInterrupted { get; set; }
    public int PressureReversed { get; set; }
    public int PressureMashCatches { get; set; }
    public int CaughtMashing { get; set; }
    public int PressureDdCatches { get; set; }
    public int EscapeAttempts { get; set; }
    public int EscapeSuccess { get; set; }
    public int EscapeFailed { get; set; }
    public int EscapeUnresolved { get; set; }
    public int UnfinishedHits { get; set; }
    public int CircleOrDownHits { get; set; }
    public int CounterHits { get; set; }
    public int TakenCounterHits { get; set; }
    public bool CounterKnown { get; set; } = true;
    public double Distance { get; set; }
    public double Seconds => CombatFrames * ReplayTiming.SecondsPerFrame;
}

public sealed class ReplaySession
{
    private readonly Dictionary<int, SideStats> totals = [];
    private int? roundCount;
    public required string Source { get; init; }
    public string TraceOrigin { get; internal set; } = "来源未知";
    public ReplayHeader? Header { get; init; }
    public required IReadOnlyList<Frame> Frames { get; init; }
    public required IReadOnlyList<ReplayEvent> Events { get; init; }
    public required IReadOnlyList<Combo> Combos { get; init; }
    public required IReadOnlyList<Pressure> Pressures { get; init; }
    public required IReadOnlyList<EscapeAttemptResult> EscapeAttempts { get; init; }
    public required IReadOnlyList<PressureGap> PressureGaps { get; init; }
    public required IReadOnlyList<Curve> Curves { get; init; }
    public required IReadOnlyList<CardEvent> CardEvents { get; init; }
    public required IReadOnlyList<SideStats> Stats { get; init; }
    public int RoundCount => roundCount ??= Frames.Where(x => x.MatchState == 2)
        .Select(x => x.Round).Distinct().Count();
    public double Duration => Frames.Count * ReplayTiming.SecondsPerFrame;
    public SideStats Total(int side)
    {
        lock (totals)
        {
            if (totals.TryGetValue(side, out var cached)) return cached;
            return totals[side] = BuildTotal(side);
        }
    }
    private SideStats BuildTotal(int side)
    {
        var rows = Stats.Where(x => x.Side == side).ToArray();
        var total = new SideStats {
            Side = side, Round = -1,
            CombatFrames = rows.Sum(x => x.CombatFrames), Damage = rows.Sum(x => x.Damage),
            TakenDamage = rows.Sum(x => x.TakenDamage), Inputs = rows.Sum(x => x.Inputs),
            EffectiveInputs = rows.Sum(x => x.EffectiveInputs),
            RedundantInputs = rows.Sum(x => x.RedundantInputs),
            ForwardDashes = rows.Sum(x => x.ForwardDashes),
            BackDashes = rows.Sum(x => x.BackDashes),
            DdAttempts = rows.Sum(x => x.DdAttempts),
            DdSuccess = rows.Sum(x => x.DdSuccess),
            ChipDamage = rows.Sum(x => x.ChipDamage),
            TakenChipDamage = rows.Sum(x => x.TakenChipDamage),
            WeatherDamage = rows.Sum(x => x.WeatherDamage),
            TakenWeatherDamage = rows.Sum(x => x.TakenWeatherDamage),
            TyphoonDamage = rows.Sum(x => x.TyphoonDamage),
            TakenTyphoonDamage = rows.Sum(x => x.TakenTyphoonDamage),
            AttackDamage = rows.Sum(x => x.AttackDamage),
            TakenAttackDamage = rows.Sum(x => x.TakenAttackDamage),
            Hits = rows.Sum(x => x.Hits), TakenHits = rows.Sum(x => x.TakenHits),
            Blocks = rows.Sum(x => x.Blocks), Pressures = rows.Sum(x => x.Pressures),
            CardsUsed = rows.Sum(x => x.CardsUsed), Distance = rows.Sum(x => x.Distance),
            GuardFrames = rows.Sum(x => x.GuardFrames),
            DefenseEpisodes = rows.Sum(x => x.DefenseEpisodes),
            StandGuards = rows.Sum(x => x.StandGuards),
            CrouchGuards = rows.Sum(x => x.CrouchGuards),
            WrongGuards = rows.Sum(x => x.WrongGuards),
            GuardLevelKnown = rows.Length > 0 && rows.All(x => x.GuardLevelKnown),
            ChargedMixupsFaced = rows.Sum(x => x.ChargedMixupsFaced),
            ChargedMixupsBlocked = rows.Sum(x => x.ChargedMixupsBlocked),
            ChargedMixupsWrongGuard = rows.Sum(x => x.ChargedMixupsWrongGuard),
            ChargedMixupsKnown = rows.Length > 0 && rows.All(x => x.ChargedMixupsKnown),
            LowSpiritFrames = rows.Sum(x => x.LowSpiritFrames),
            LongestLowSpiritFrames = rows.Length == 0 ? 0 : rows.Max(x => x.LongestLowSpiritFrames),
            EmptySpirit = rows.Sum(x => x.EmptySpirit), BrokenOrbs = rows.Sum(x => x.BrokenOrbs),
            Techs = rows.Sum(x => x.Techs),
            TechTrapped = rows.Sum(x => x.TechTrapped),
            TechChases = rows.Sum(x => x.TechChases),
            TechChaseSuccess = rows.Sum(x => x.TechChaseSuccess),
            ReversalsUsed = rows.Sum(x => x.ReversalsUsed),
            ReversalsHit = rows.Sum(x => x.ReversalsHit),
            ReversalsKnown = rows.Length > 0 && rows.All(x => x.ReversalsKnown),
            TakenProjectileOpeners = rows.Sum(x => x.TakenProjectileOpeners),
            ProjectileOpenerKnown = rows.Length > 0 && rows.All(x => x.ProjectileOpenerKnown),
            SameDirectionHits = rows.Sum(x => x.SameDirectionHits),
            StartupPunished = rows.Sum(x => x.StartupPunished),
            RecoveryPunished = rows.Sum(x => x.RecoveryPunished),
            ReversalPunished = rows.Sum(x => x.ReversalPunished),
            PunishPhaseKnown = rows.Length > 0 && rows.All(x => x.PunishPhaseKnown),
            LandingCancels = rows.Sum(x => x.LandingCancels),
            LandingCancelsKnown = rows.Length > 0 && rows.All(x => x.LandingCancelsKnown),
            LimitBreaks = rows.Sum(x => x.LimitBreaks),
            LowDamageHits = rows.Sum(x => x.LowDamageHits),
            ProjectileLowDamageHits = rows.Sum(x => x.ProjectileLowDamageHits),
            TakenLowDamageHits = rows.Sum(x => x.TakenLowDamageHits),
            ProjectileLowDamageKnown = rows.Length > 0 && rows.All(x => x.ProjectileLowDamageKnown),
            LowDamageSpellHits = rows.Sum(x => x.LowDamageSpellHits),
            LowDamageSpellOnlyHits = rows.Sum(x => x.LowDamageSpellOnlyHits),
            ExtremeMeleeHits = rows.Sum(x => x.ExtremeMeleeHits),
            TakenExtremeMeleeHits = rows.Sum(x => x.TakenExtremeMeleeHits),
            ExtremeMeleeKnown = rows.Length > 0 && rows.All(x => x.ExtremeMeleeKnown),
            TakenExtremeMeleeKnown = rows.Length > 0 && rows.All(x => x.TakenExtremeMeleeKnown),
            WakeupPressureFailures = rows.Sum(x => x.WakeupPressureFailures),
            WakeupPressures = rows.Sum(x => x.WakeupPressures),
            WakeupPressureSuccess = rows.Sum(x => x.WakeupPressureSuccess),
            WakeupPressureInterrupted = rows.Sum(x => x.WakeupPressureInterrupted),
            WakeupPressureReversed = rows.Sum(x => x.WakeupPressureReversed),
            PressureInterrupted = rows.Sum(x => x.PressureInterrupted),
            PressureReversed = rows.Sum(x => x.PressureReversed),
            PressureMashCatches = rows.Sum(x => x.PressureMashCatches),
            CaughtMashing = rows.Sum(x => x.CaughtMashing),
            PressureDdCatches = rows.Sum(x => x.PressureDdCatches),
            EscapeAttempts = rows.Sum(x => x.EscapeAttempts),
            EscapeSuccess = rows.Sum(x => x.EscapeSuccess),
            EscapeFailed = rows.Sum(x => x.EscapeFailed),
            EscapeUnresolved = rows.Sum(x => x.EscapeUnresolved),
            UnfinishedHits = rows.Sum(x => x.UnfinishedHits),
            CircleOrDownHits = rows.Sum(x => x.CircleOrDownHits),
            CounterHits = rows.Sum(x => x.CounterHits),
            TakenCounterHits = rows.Sum(x => x.TakenCounterHits),
            CounterKnown = rows.All(x => x.CounterKnown)
        };
        foreach (var row in rows)
        {
            Merge(total.DDirections, row.DDirections);
            Merge(total.HighJumps, row.HighJumps);
            Merge(total.SmallJumps, row.SmallJumps);
        }
        return total;
    }
    private static void Merge(Dictionary<string, int> target, Dictionary<string, int> source)
    {
        foreach (var (key, value) in source)
            target[key] = target.GetValueOrDefault(key) + value;
    }
}

public static class TraceReader
{
    public static List<Frame> Read(string path)
        => ReadCore(path, null, 0);

    // A branch is identical to its validated baseline before the takeover frame.
    // Keep those frames and only parse the new tail of the engine trace.
    public static List<Frame> ReadWithPrefix(string path, IReadOnlyList<Frame> baseline,
        int firstChangedFrame)
    {
        if (firstChangedFrame < 0 || firstChangedFrame > baseline.Count)
            throw new ArgumentOutOfRangeException(nameof(firstChangedFrame));
        return ReadCore(path, baseline, firstChangedFrame);
    }

    public static List<Frame> ReadTailWithPrefix(string path, IReadOnlyList<Frame> baseline,
        int firstChangedFrame)
    {
        if (firstChangedFrame < 0 || firstChangedFrame > baseline.Count)
            throw new ArgumentOutOfRangeException(nameof(firstChangedFrame));
        return ReadCore(path, baseline, firstChangedFrame, tailOnly: true);
    }

    private static List<Frame> ReadCore(string path, IReadOnlyList<Frame>? baseline,
        int firstChangedFrame, bool tailOnly = false)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var headerLine = reader.ReadLine() ?? throw new InvalidDataException("轨迹为空。");
        var names = ParseCsv(headerLine);
        var index = names.Select((name, position) => (name, position))
            .ToDictionary(x => x.name, x => x.position);
        foreach (var field in new[] { "frame", "match_state", "round", "weather",
            "p1_char", "p1_action", "p1_hp", "p2_char", "p2_action", "p2_hp" })
            if (!index.ContainsKey(field)) throw new InvalidDataException($"缺少字段 {field}。");
        int Column(string name) => index.GetValueOrDefault(name, -1);
        int[] FighterColumns(string prefix) =>
        [
            Column(prefix + "char"), Column(prefix + "x"), Column(prefix + "y"),
            Column(prefix + "action"), Column(prefix + "action_frame"),
            Column(prefix + "hp"), Column(prefix + "spirit"), Column(prefix + "input"),
            Column(prefix + "combo_count"), Column(prefix + "combo_damage"),
            Column(prefix + "combo_limit"), Column(prefix + "card_count"),
            Column(prefix + "card_gauge"), Column(prefix + "hit_stop"),
            Column(prefix + "collision_type"), Column(prefix + "damage_limited"),
            Column(prefix + "frame_flags"), Column(prefix + "hand_ids"),
            Column(prefix + "used_ids"), Column(prefix + "dir"),
            Column(prefix + "max_spirit"), Column(prefix + "broken_orb"),
            Column(prefix + "card_selected"), Column(prefix + "charged_attack"),
            Column(prefix + "attack_flags"), Column(prefix + "counter_vulnerable"),
            Column(prefix + "untech"), Column(prefix + "active_melee_reach"),
            Column(prefix + "melee_max_reach")
        ];
        var commonColumns = new[] { Column("frame"), Column("match_state"),
            Column("round"), Column("weather") };
        var p1Columns = FighterColumns("p1_");
        var p2Columns = FighterColumns("p2_");
        var frames = baseline is null ? new List<Frame>() :
            new List<Frame>(baseline.Take(firstChangedFrame));
        string? line;
        var lineNumber = 1;
        var dataRows = 0;
        var row = new CsvRow(names.Count);
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;
            if (!tailOnly && dataRows < firstChangedFrame) {
                dataRows++;
                continue;
            }
            dataRows++;
            row.Read(line);
            if (row.Count != names.Count)
                throw new InvalidDataException($"轨迹第 {lineNumber} 行字段数量错误。");
            int I(int column, int fallback = 0) =>
                column >= 0 && row[column].Length > 0
                    ? int.Parse(row[column], CultureInfo.InvariantCulture) : fallback;
            double D(int column) =>
                column >= 0 && row[column].Length > 0
                    ? double.Parse(row[column], CultureInfo.InvariantCulture) : 0;
            int[]? Cards(int column)
            {
                if (column < 0 || row[column].SequenceEqual("?")) return null;
                if (row[column].Length == 0) return [];
                var cards = row[column];
                var result = new int[cards.Count('|') + 1];
                var next = 0;
                foreach (var part in cards.Split('|'))
                    result[next++] = int.Parse(cards[part], CultureInfo.InvariantCulture);
                return result;
            }
            Fighter FighterAt(int[] c) => new(
                I(c[0]), D(c[1]), D(c[2]), I(c[3]),
                I(c[4]), I(c[5]), I(c[6]), I(c[7]),
                I(c[8]), I(c[9]), I(c[10]), I(c[11]),
                I(c[12]), I(c[13]), I(c[14]), I(c[15]), I(c[16], -1),
                Cards(c[17]), Cards(c[18]), I(c[19], -1), I(c[20], -1),
                I(c[21], -1), I(c[22], -1), I(c[23], -1), I(c[24], -1),
                I(c[25], -1), I(c[26], -1), I(c[27], -1), I(c[28], -1));
            try
            {
                frames.Add(new Frame(frames.Count, I(commonColumns[0]), I(commonColumns[1]),
                    I(commonColumns[2]), I(commonColumns[3]), FighterAt(p1Columns), FighterAt(p2Columns)));
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"轨迹第 {lineNumber} 行数值错误：{ex.Message}", ex);
            }
        }
        if (!tailOnly && dataRows < firstChangedFrame)
            throw new InvalidDataException("分支轨迹短于接管帧。");
        return frames;
    }

    // Native traces are unquoted numeric CSV. Reuse column offsets instead of
    // allocating a string for every number; quoted imports keep the CSV parser.
    private sealed class CsvRow(int capacity)
    {
        private readonly Range[] cells = new Range[capacity];
        private string line = "";
        private List<string>? quoted;
        public int Count { get; private set; }
        public ReadOnlySpan<char> this[int column] => quoted is null
            ? line.AsSpan(cells[column]) : quoted[column].AsSpan();

        public void Read(string value)
        {
            line = value;
            quoted = null;
            if (value.Contains('"'))
            {
                quoted = ParseCsv(value);
                Count = quoted.Count;
                return;
            }
            Count = 0;
            var start = 0;
            for (var i = 0; i <= value.Length; i++)
            {
                if (i < value.Length && value[i] != ',') continue;
                if (Count < cells.Length) cells[Count] = start..i;
                Count++;
                start = i + 1;
            }
        }
    }

    private static List<string> ParseCsv(string line)
    {
        var result = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { result.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        if (quoted) throw new InvalidDataException("轨迹 CSV 引号未闭合。");
        result.Add(cell.ToString());
        return result;
    }
}

public static class ReplayAnalyzer
{
    private static readonly HashSet<int> HitReaction =
    [50, 51, 52, 53, 56, 57, 58, 62, 63, 64, 70, 71, 72, 73, 74, 75,
     76, 77, 78, 88, 89, 97, 98, 99, 100, 143, 145];
    private static bool Guard(int action) => action is >= 150 and <= 166;
    private static bool Hit(int action) => HitReaction.Contains(action);
    private static bool HighLowMelee(Fighter fighter) => fighter.Action is >= 300 and < 400 &&
        fighter.AttackFlags >= 0 && ((fighter.AttackFlags & 0x2) != 0) !=
        ((fighter.AttackFlags & 0x4) != 0);
    private static bool ChargedMelee(Fighter fighter) => fighter.ChargedAttack > 0 &&
        fighter.Action is 300 or 301 or 302 or 304 or 331;
    private static bool BorderEscape(int action) => action is 220 or 222 or 223 or 224 or 225 or 226;
    private static bool BlockContact(Frame previous, Frame frame, int side)
    {
        var fighter = frame.Side(side);
        var old = previous.Side(side);
        var opponent = frame.Side(1-side);
        var oldOpponent = previous.Side(1-side);
        return Guard(fighter.Action) && (!Guard(old.Action) ||
            fighter.ActionFrame < old.ActionFrame && fighter.ActionFrame <= 2 &&
            (fighter.Spirit < old.Spirit || fighter.HitStop > old.HitStop ||
             opponent.Collision == 2 && oldOpponent.Collision != 2 ||
             fighter.Action != old.Action));
    }
    private static bool Tech(int action) => action is 180 or 181 or 197 or 198 or 199;
    private static bool Actionable(int action) => action <= 10 || action is 180 or 181 ||
        action is >= 197 and <= 226 || action >= 300;
    public static int ComboOpeningAction(IReadOnlyList<Frame> frames, Combo combo)
    {
        var impact = combo.Impacts[0];
        var current = frames[impact].Side(combo.Side).Action;
        if (current is >= 300 and < 670) return current;
        var lower = Math.Max(0, impact - 120);
        for (var i = impact - 1; i >= lower; --i)
        {
            var frame = frames[i];
            if (frame.Round != combo.Round) break;
            var action = frame.Side(combo.Side).Action;
            if (action is >= 300 and < 670) return action;
        }
        return -1;
    }
    private static (bool Spell, bool OnlySpell) SpellContext(IReadOnlyList<Frame> frames,
        Combo combo)
    {
        var lastImpact = combo.Impacts[^1];
        int? lastSpell = null;
        var otherAttack = false;
        Fighter? previous = null;
        var firstRelevant = Math.Max(0, combo.Start - 120);
        for (var i = firstRelevant - 1; i >= 0; i--)
        {
            var frame = frames[i];
            if (frame.Round != combo.Round || frame.MatchState is not (2 or 3 or 5))
                continue;
            previous = frame.Side(combo.Side);
            break;
        }
        for (var i = firstRelevant; i <= lastImpact; i++)
        {
            var frame = frames[i];
            if (frame.Round != combo.Round || frame.MatchState is not (2 or 3 or 5))
                continue;
            var fighter = frame.Side(combo.Side);
            if (frame.Ordinal >= combo.Start - 120)
            {
                var started = previous is null || fighter.Action != previous.Action ||
                    fighter.ActionFrame < previous.ActionFrame && fighter.ActionFrame <= 2;
                if (fighter.Action is >= 600 and <= 619 && started)
                { lastSpell = frame.Ordinal; otherAttack = false; }
                else if (lastSpell is not null && previous is not null && started &&
                         fighter.Action is >= 300 and < 600)
                    otherAttack = true;
                if (fighter.Action is >= 650 and < 670 && combo.Impacts.Contains(frame.Ordinal))
                    lastSpell = frame.Ordinal;
            }
            previous = fighter;
        }
        return (lastSpell is not null, lastSpell is not null && !otherAttack);
    }
    private static double Cards(Fighter f) => Math.Min(5, f.CardCount + Math.Clamp(f.CardGauge, 0, 500) / 500.0);

    private static void ReclassifyDamage(IReadOnlyList<Frame> frames, IReadOnlyList<Combo> combos,
        Dictionary<(int side, int round), SideStats> stats)
    {
        foreach (var row in stats.Values)
            row.ChipDamage = row.WeatherDamage = row.TyphoonDamage = 0;
        var records = new Dictionary<int, List<Frame>>();
        int? phaseRound = null;
        foreach (var frame in frames)
        {
            if (frame.MatchState == 2 && frame.Round >= 0) phaseRound = frame.Round;
            else if (frame.MatchState is not (3 or 5)) continue;
            if (phaseRound is null) continue;
            if (!records.TryGetValue(phaseRound.Value, out var list))
                records[phaseRound.Value] = list = [];
            list.Add(frame);
        }
        foreach (var (round, list) in records)
            for (var side = 0; side < 2; side++)
            {
                if (!stats.TryGetValue((side, round), out var row)) continue;
                var impacts = combos.Where(c => c.Round == round && c.Side == side)
                    .SelectMany(c => c.Impacts).ToHashSet();
                for (var i = 1; i < list.Count; i++)
                {
                    var frame = list[i];
                    var defender = frame.Side(1 - side);
                    var loss = Math.Max(0, list[i - 1].Side(1 - side).Hp - defender.Hp);
                    if (loss == 0) continue;
                    if (Guard(defender.Action)) row.ChipDamage += loss;
                    else if (frame.Weather == 13 ||
                             frame.Weather is 15 or 17 && !impacts.Contains(frame.Ordinal))
                    {
                        row.WeatherDamage += loss;
                        if (frame.Weather == 13) row.TyphoonDamage += loss;
                    }
                }
            }
    }

    public static ReplaySession Analyze(string source, ReplayHeader? header,
        IReadOnlyList<Frame> frames, string traceOrigin = "来源未知")
    {
        var events = new List<ReplayEvent>();
        var combos = new List<Combo>();
        var pressures = new List<Pressure>();
        var curves = new List<Curve>();
        var stats = new Dictionary<(int side, int round), SideStats>();
        var livePressure = new PressureBuilder?[2];
        var recentAttack = new List<(int ordinal, int action)>[2] { [], [] };
        var lowSpiritRun = new int[2];
        var limitLatched = new bool[2];
        var lastD = new int?[2];
        var pendingDd = new int?[2];
        Frame? previous = null;
        int? phaseRound = null;
        foreach (var frame in frames)
        {
            var active = frame.MatchState == 2 && frame.Round >= 0;
            var tail = !active && frame.MatchState is 3 or 5 && phaseRound is not null;
            var consecutive = previous is not null &&
                (active && previous.MatchState == 2 && previous.Round == frame.Round ||
                 tail && previous.MatchState is 2 or 3 or 5);
            if (!consecutive)
            {
                for (var side = 0; side < 2; side++)
                {
                    FinishPressure(side, previous?.Ordinal ?? frame.Ordinal, "回合结束");
                }
            }
            if (active) phaseRound = frame.Round;
            if (!active)
            {
                if (tail && consecutive)
                {
                    for (var side = 0; side < 2; side++)
                    {
                        var attacker = frame.Side(side);
                        var defender = frame.Side(1-side);
                        var oldAttacker = previous!.Side(side);
                        var oldDefender = previous.Side(1-side);
                        var key = (side, phaseRound!.Value);
                        if (!stats.TryGetValue(key, out var row))
                            stats[key] = row = new SideStats { Side = side, Round = phaseRound.Value };
                        if (!Hit(defender.Action) && defender.Hp > 0) limitLatched[side] = false;
                        var circleStarted = defender.DamageLimited > 0 && oldDefender.DamageLimited <= 0;
                        var limitStarted = attacker.ComboLimit >= 100 && oldAttacker.ComboLimit < 100 &&
                            attacker.ComboCount > 0 && attacker.ComboDamage > 0 && Hit(defender.Action);
                        if (!limitLatched[side] && (limitStarted ||
                            circleStarted && (Hit(defender.Action) || defender.Hp <= 0)))
                        {
                            limitLatched[side] = true;
                            row.LimitBreaks++;
                            events.Add(new(frame.Ordinal, phaseRound.Value, side, "出圈",
                                $"连段保护 {attacker.ComboLimit}", "回合结束阶段确认受击方出圈"));
                        }
                        var loss = Math.Max(0, oldDefender.Hp-defender.Hp);
                        if (loss == 0) continue;
                        row.Damage += loss;
                        var defenderKey = (1-side, phaseRound.Value);
                        if (!stats.TryGetValue(defenderKey, out var defenderRow))
                            stats[defenderKey] = defenderRow = new SideStats { Side = 1-side, Round = phaseRound.Value };
                        defenderRow.TakenDamage += loss;
                    }
                }
                previous = frame;
                continue;
            }
            if (frames.Count < 10000 || frame.Ordinal % 12 == 0 || curves.Count == 0)
                curves.Add(new Curve(frame.Ordinal, frame.Round, frame.P1.Hp, frame.P2.Hp,
                    frame.P1.Spirit, frame.P2.Spirit, Cards(frame.P1), Cards(frame.P2)));
            for (var side = 0; side < 2; side++)
            {
                var fighter = frame.Side(side);
                var opponent = frame.Side(1-side);
                var key = (side, frame.Round);
                if (!stats.TryGetValue(key, out var row))
                    stats[key] = row = new SideStats { Side = side, Round = frame.Round };
                row.CombatFrames++;
                if (opponent.AttackFlags >= 0) row.GuardLevelKnown = true;
                if (opponent.ChargedAttack >= 0) row.ChargedMixupsKnown = true;
                if (Guard(fighter.Action)) row.GuardFrames++;
                if (!consecutive) {
                    lowSpiritRun[side] = 0; limitLatched[side] = false;
                    lastD[side] = null; pendingDd[side] = null;
                }
                lowSpiritRun[side] = fighter.Spirit < 400 ? lowSpiritRun[side] + 1 : 0;
                if (fighter.Spirit < 400) row.LowSpiritFrames++;
                row.LongestLowSpiritFrames = Math.Max(row.LongestLowSpiritFrames, lowSpiritRun[side]);
                if (!consecutive && Guard(fighter.Action)) row.DefenseEpisodes++;
                if (!consecutive && Tech(fighter.Action)) row.Techs++;
                if (!consecutive) continue;
                var old = previous!.Side(side);
                var oldOpponent = previous.Side(1-side);
                var actionStarted = fighter.Action != old.Action ||
                    fighter.ActionFrame < old.ActionFrame && fighter.ActionFrame <= 2;
                if (actionStarted && fighter.Action is 200 or 202 or 206) row.ForwardDashes++;
                if (actionStarted && fighter.Action is 201 or 203 or 207) row.BackDashes++;
                if ((fighter.Input & 0x80) != 0 && (old.Input & 0x80) == 0)
                {
                    var guarding = Guard(fighter.Action) || Guard(old.Action);
                    if (lastD[side] is int earlierD && frame.Ordinal - earlierD <= 20 &&
                        guarding && (fighter.Input & 0xf) != 0)
                    {
                        row.DdAttempts++;
                        pendingDd[side] = frame.Ordinal;
                        lastD[side] = null;
                        events.Add(new(frame.Ordinal, frame.Round, side, "DD 尝试", "防御中二次 D",
                            "20 帧内第二次按下 D 且伴随方向输入"));
                    }
                    else lastD[side] = frame.Ordinal;
                }
                if (BorderEscape(fighter.Action) && !BorderEscape(old.Action))
                {
                    if (pendingDd[side] is not int attempt || frame.Ordinal - attempt > 20)
                    {
                        row.DdAttempts++;
                        events.Add(new(frame.Ordinal, frame.Round, side, "DD 尝试", "引擎确认回避结界",
                            "未捕获有效输入起点时，按原生动作补记尝试"));
                    }
                    row.DdSuccess++;
                    pendingDd[side] = null;
                    events.Add(new(frame.Ordinal, frame.Round, side, "DD 成功", $"动作 {fighter.Action}",
                        "引擎进入回避结界动作"));
                }
                if (Guard(fighter.Action) && !Guard(old.Action)) row.DefenseEpisodes++;
                if (fighter.Spirit == 0 && old.Spirit > 0) row.EmptySpirit++;
                if (fighter.MaxSpirit >= 0 && old.MaxSpirit >= 0 &&
                    old.MaxSpirit > fighter.MaxSpirit)
                {
                    var broken = (old.MaxSpirit - fighter.MaxSpirit) / 200;
                    row.BrokenOrbs += broken;
                    if (broken > 0)
                        events.Add(new(frame.Ordinal, frame.Round, side, "破灵珠",
                            $"减少 {broken} 颗", "灵力上限逐帧下降，每 200 点计一颗"));
                }
                if (Tech(fighter.Action) && !Tech(old.Action)) row.Techs++;
                if (!Hit(opponent.Action) && opponent.Hp > 0) limitLatched[side] = false;
                var circleStarted = opponent.DamageLimited > 0 && oldOpponent.DamageLimited <= 0;
                var limitStarted = fighter.ComboLimit >= 100 && old.ComboLimit < 100 &&
                    fighter.ComboCount > 0 && fighter.ComboDamage > 0 && Hit(opponent.Action);
                if (!limitLatched[side] && (limitStarted ||
                    circleStarted && (Hit(opponent.Action) || opponent.Hp <= 0)))
                {
                    limitLatched[side] = true;
                    row.LimitBreaks++;
                    events.Add(new(frame.Ordinal, frame.Round, side, "出圈",
                        $"连段保护 {fighter.ComboLimit}", "受击中连段保护首次达到 100，或受击方原生出圈位首次有效"));
                }
                if (fighter.Action is >= 300 and <= 669 &&
                    (fighter.Action != old.Action || fighter.ActionFrame < old.ActionFrame))
                    recentAttack[side].Add((frame.Ordinal, fighter.Action));
                recentAttack[side].RemoveAll(x => frame.Ordinal - x.ordinal > 180);
                var hpLoss = Math.Max(0, old.Hp - fighter.Hp);
                var opponentLoss = Math.Max(0, oldOpponent.Hp - opponent.Hp);
                row.Damage += opponentLoss;
                row.TakenDamage += hpLoss;
                row.Inputs += System.Numerics.BitOperations.PopCount((uint)((fighter.Input & ~old.Input) & 0x3ff));
                if (!Hit(fighter.Action) && !Guard(fighter.Action))
                    row.Distance += Math.Sqrt(Math.Pow(fighter.X-old.X, 2) + Math.Pow(fighter.Y-old.Y, 2));
                if (fighter.CardCount < old.CardCount)
                {
                    row.CardsUsed += old.CardCount - fighter.CardCount;
                    events.Add(new(frame.Ordinal, frame.Round, side, "卡槽减少",
                        $"{old.CardCount} → {fighter.CardCount}", "实际卡牌身份需结合手牌和宣言动作核对"));
                }
                var contact = BlockContact(previous, frame, side);
                if (livePressure[side] is { } ongoing)
                {
                    if (Guard(opponent.Action) && BlockContact(previous, frame, 1-side))
                        ongoing.AttackerGuardOrdinal = frame.Ordinal;
                    if (BorderEscape(fighter.Action))
                    {
                        if (!BorderEscape(old.Action)) ongoing.DefenderDdOrdinal = frame.Ordinal;
                    }
                    if (fighter.Y > 0.1 || fighter.Action is 6 or 7 or 8 or 9 or 208 or 209 or 210)
                    {
                        ongoing.DefenderJumpOrdinal ??= frame.Ordinal;
                    }
                    var defenderHit = Hit(fighter.Action) && !Hit(old.Action) &&
                        (fighter.Hp < old.Hp || opponent.ComboDamage > oldOpponent.ComboDamage);
                    var attackerHit = Hit(opponent.Action) && !Hit(oldOpponent.Action) &&
                        (opponent.Hp < oldOpponent.Hp || fighter.ComboDamage > old.ComboDamage);
                    if (defenderHit || attackerHit)
                    {
                        var reason = attackerHit ? "被反击" :
                            ongoing.AttackerGuardOrdinal is int guardAt && frame.Ordinal - guardAt <= 30 ? "确反" :
                            BorderEscape(old.Action) || ongoing.DefenderDdOrdinal is int ddAt &&
                                frame.Ordinal - ddAt <= 20 ? "抓dd" :
                            old.Y > 0.1 || old.Action is 6 or 7 or 8 or 9 or 208 or 209 or 210 ? "抓跳" :
                            old.Action >= 200 && !Guard(old.Action) ? "抓乱动" : "命中";
                        FinishPressure(side, frame.Ordinal, reason);
                    }
                    else if (!contact && !Guard(fighter.Action) && fighter.HitStop == 0)
                    {
                        ongoing.FreeStart ??= frame.Ordinal;
                        ongoing.FreeFrames++;
                        if (ongoing.FreeFrames >= 60)
                        {
                            var freeStart = ongoing.FreeStart.Value;
                            var evidenceEnd = freeStart + 10;
                            var reason = ongoing.DefenderDdOrdinal is int ddAt && ddAt <= evidenceEnd ? "dd" :
                                ongoing.DefenderJumpOrdinal is int jumpAt && jumpAt <= evidenceEnd ? "跳走" :
                                ongoing.AttackerGuardOrdinal is int guardAt && guardAt <= evidenceEnd ? "转防" :
                                recentAttack[1-side].Any(x => x.ordinal > ongoing.LastContact &&
                                    x.ordinal <= evidenceEnd)
                                    ? "后续未接" : "脱离";
                            FinishPressure(side, freeStart, reason, evidenceEnd);
                        }
                    }
                    else if (!contact)
                    {
                        ongoing.FreeFrames = 0;
                        ongoing.FreeStart = null;
                    }
                }
                if (contact)
                {
                    row.Blocks++;
                    if (HighLowMelee(opponent))
                    {
                        if (fighter.Action is >= 150 and <= 153 or >= 159 and <= 162)
                            row.StandGuards++;
                        else if (fighter.Action is >= 154 and <= 157 or >= 163 and <= 166)
                            row.CrouchGuards++;
                        if (fighter.Action is >= 159 and <= 166)
                        {
                            row.WrongGuards++;
                            events.Add(new(frame.Ordinal, frame.Round, side, "错防",
                                $"对手动作 {opponent.Action}",
                                "原生上下段标志明确的体术确认防御接触，防御动作处于错防段"));
                        }
                    }
                    if (ChargedMelee(opponent))
                    {
                        row.ChargedMixupsFaced++;
                        if (fighter.Action is >= 159 and <= 166)
                            row.ChargedMixupsWrongGuard++;
                        else
                            row.ChargedMixupsBlocked++;
                    }
                    events.Add(new(frame.Ordinal, frame.Round, side, "防御接触",
                        $"防御动作 {fighter.Action} / 对手动作 {opponent.Action}",
                        "防御动作起点或动作帧重启，伴随灵力、停帧或碰撞变化"));
                    var pressure = livePressure[side];
                    if (pressure is null)
                    {
                        livePressure[side] = pressure = new PressureBuilder(frame.Ordinal, frame.Round, 1-side, side);
                    }
                    pressure.LastContact = frame.Ordinal;
                    pressure.Contacts++;
                    pressure.FreeFrames = 0;
                    pressure.FreeStart = null;
                    pressure.DefenderDdOrdinal = pressure.DefenderJumpOrdinal =
                        pressure.AttackerGuardOrdinal = null;
                    var attackerStarts = recentAttack[1-side];
                    var sameAction = attackerStarts.LastOrDefault(x => x.action == opponent.Action);
                    var projectile = attackerStarts.LastOrDefault(x => x.action is >= 400 and < 620);
                    var move = opponent.Action is >= 300 and <= 669 &&
                               (opponent.Collision == 2 || sameAction != default)
                        ? sameAction == default ? opponent.Action : sameAction.action
                        : projectile == default ? -1 : projectile.action;
                    if (pressure.Moves.Count == 0 || pressure.Moves[^1] != move)
                        pressure.Moves.Add(move);
                }
            }
            if (consecutive && frame.Weather != previous!.Weather)
                events.Add(new(frame.Ordinal, frame.Round, -1, "天气切换",
                    $"{previous.Weather} → {frame.Weather}", "原始天气字段变化"));
            previous = frame;
        }
        for (var side = 0; side < 2; side++)
        {
            FinishPressure(side, frames.Count == 0 ? 0 : frames[^1].Ordinal, "轨迹结束");
        }
        combos.AddRange(ComboDetector.Detect(frames));
        ReclassifyDamage(frames, combos, stats);
        foreach (var combo in combos)
        {
            var defending = stats[(1 - combo.Side, combo.Round)];
            if (combo.Start > 0)
            {
                var own = frames[combo.Start - 1].Side(1 - combo.Side);
                var foe = frames[combo.Start - 1].Side(combo.Side);
                if (own.Action is >= 300 and < 620 && own.FrameFlags >= 0 &&
                    (own.FrameFlags & 0x10) == 0 && own.ActiveMeleeReach <= 0 &&
                    own.Collision != 2)
                {
                    var priorActive = false;
                    for (var i = Math.Max(0, combo.Start - 90); i < combo.Start; i++)
                    {
                        var past = frames[i];
                        if (past.Round != combo.Round) continue;
                        var fighter = past.Side(1 - combo.Side);
                        if (fighter.Action == own.Action && fighter.ActionFrame < own.ActionFrame &&
                            (fighter.ActiveMeleeReach > 0 || fighter.Collision == 2))
                        { priorActive = true; break; }
                    }
                    if (priorActive) defending.RecoveryPunished++;
                    else defending.StartupPunished++;
                    events.Add(new(combo.Start, combo.Round, 1 - combo.Side,
                        priorActive ? "技能硬直被攻击" : "技能前摇被攻击",
                        $"动作 {own.Action}",
                        priorActive ? "同一动作已出现活动攻击框，当前无攻击框且不可防御" :
                            "同一动作尚未出现活动攻击框，当前无攻击框且不可防御"));
                }
                if (foe.FrameFlags >= 0 && foe.Action is >= 300 and < 620 &&
                    (own.ActiveMeleeReach > 0 && (foe.FrameFlags & 0x2000) != 0 ||
                     own.Collision == 2 && (foe.FrameFlags & 0x4000) != 0))
                {
                    var forward = (foe.X - own.X) * (own.Direction == 1 ? 1 : -1);
                    if (own.Collision == 2 ||
                        forward >= 0 && forward <= own.ActiveMeleeReach &&
                        Math.Abs(foe.Y - own.Y) <= 80)
                    {
                        defending.ReversalPunished++;
                        events.Add(new(combo.Start, combo.Round, 1 - combo.Side, "被凹",
                            $"自身动作 {own.Action}，对手动作 {foe.Action}",
                            "自身活动攻击框接触对手无敌状态，随后实际被反击"));
                    }
                }
            }
            foreach (var impact in combo.Impacts)
            {
                if (impact <= 0) continue;
                var before = frames[impact - 1];
                var facing = before.Side(combo.Side).Direction;
                if (facing is not (-1 or 1) || facing != before.Side(1 - combo.Side).Direction)
                    continue;
                defending.SameDirectionHits++;
                events.Add(new(impact, combo.Round, 1 - combo.Side, "同向被攻击",
                    $"命中前双方朝向 {facing}", "双方在确认命中前一帧朝向相同"));
            }
            if (combo.Impacts.Any(i => frames[i].Weather == 13)) continue;
            stats[(combo.Side, combo.Round)].Hits++;
            defending.TakenHits++;
            stats[(combo.Side, combo.Round)].AttackDamage += combo.Damage;
            defending.TakenAttackDamage += combo.Damage;
            var firstImpact = frames[combo.Impacts[0]].Side(combo.Side);
            var openingAction = ComboOpeningAction(frames, combo);
            if (openingAction is >= 400 and < 500 ||
                openingAction is >= 500 and < 670 && firstImpact.Action == openingAction &&
                firstImpact.ActiveMeleeReach == 0)
            {
                defending.TakenProjectileOpeners++;
                events.Add(new(combo.Start, combo.Round, 1 - combo.Side, "被弹幕起手",
                    $"首招动作 {openingAction}", "确认受击首招为弹幕，或原生判定无近战框的必杀／符卡"));
            }
            if (combo.Impacts.Any(i => ChargedMelee(frames[i].Side(combo.Side))))
                stats[(1 - combo.Side, combo.Round)].ChargedMixupsFaced++;
            foreach (var impact in combo.Impacts)
            {
                events.Add(new(impact, combo.Round, combo.Side, "确认命中",
                    $"实际损血 {Math.Max(0, frames[impact - 1].Side(1 - combo.Side).Hp - frames[impact].Side(1 - combo.Side).Hp)}",
                    "HP 下降且引擎连段字段发生变化"));
            }
        }
        foreach (var row in stats.Values)
        {
            row.ProjectileOpenerKnown = frames.Where(f => f.Round == row.Round && f.MatchState == 2)
                .All(f => f.Side(1 - row.Side).ActiveMeleeReach >= 0);
            row.ProjectileLowDamageKnown = frames.Where(f => f.Round == row.Round && f.MatchState == 2)
                .All(f => f.Side(row.Side).ActiveMeleeReach >= 0);
            row.PunishPhaseKnown = frames.Any(f => f.Round == row.Round && f.MatchState == 2 &&
                f.Side(row.Side).FrameFlags >= 0 && f.Side(row.Side).ActiveMeleeReach >= 0);
        }
        var counterCandidates = combos.Where(combo => combo.Start > 0 && combo.Start < frames.Count &&
            frames[combo.Start].Weather != 16 &&
            frames[combo.Start - 1].Side(1 - combo.Side) is { CounterVulnerable: > 0,
                Action: >= 300 and <= 669 })
            .Select(combo => (combo.Start, combo.Side)).ToHashSet();
        foreach (var combo in combos)
        {
            if (!stats.TryGetValue((combo.Side, combo.Round), out var row)) continue;
            if (combo.Impacts.Any(i => frames[i].Weather == 13)) continue;
            if (combo.Damage < 1500)
            {
                row.LowDamageHits++;
                var projectile = combo.Impacts.Any(i =>
                    frames[i].Side(combo.Side).Action is >= 400 and < 500 ||
                    frames[i].Side(combo.Side) is { Action: >= 500 and < 600,
                        ActiveMeleeReach: 0 });
                if (projectile)
                {
                    row.ProjectileLowDamageHits++;
                    stats[(1 - combo.Side, combo.Round)].TakenLowDamageHits++;
                    events.Add(new(combo.Start, combo.Round, combo.Side, "弹幕命中",
                        $"整段伤害 {combo.Damage}", "完整连段伤害低于 1500，且含弹幕命中"));
                }
                var (spell, onlySpell) = SpellContext(frames, combo);
                if (spell)
                {
                    row.LowDamageSpellHits++;
                    events.Add(new(combo.Start, combo.Round, combo.Side, "低伤害符卡参与",
                        $"整段伤害 {combo.Damage}", "符卡宣言后 120 帧内确认命中"));
                }
                if (onlySpell) row.LowDamageSpellOnlyHits++;
            }
            if (combo.Circle || combo.Knockdown)
                row.CircleOrDownHits++;
            else row.UnfinishedHits++;
            foreach (var impact in combo.Impacts)
            {
                var attacker = frames[impact].Side(combo.Side);
                if (attacker.Action is not (>= 300 and < 400)) continue;
                var other = stats[(1 - combo.Side, combo.Round)];
                if (attacker.MeleeMaxReach <= 0)
                { row.ExtremeMeleeKnown = false; other.TakenExtremeMeleeKnown = false; continue; }
                var defender = frames[impact].Side(1 - combo.Side);
                var forward = (defender.X - attacker.X) * (attacker.Direction == 1 ? 1 : -1);
                if (forward < .9 * attacker.MeleeMaxReach) continue;
                row.ExtremeMeleeHits++;
                other.TakenExtremeMeleeHits++;
                events.Add(new(impact, combo.Round, combo.Side, "体术极限距离",
                    $"动作 {attacker.Action}，命中距离 {forward:0.0} / {attacker.MeleeMaxReach}",
                    "命中距离达到固定近战框最大前伸的 90%"));
            }
            if (combo.Start <= 0 || combo.Start >= frames.Count) continue;
            var defenderBefore = frames[combo.Start - 1].Side(1 - combo.Side);
            if (defenderBefore.CounterVulnerable < 0)
            {
                row.CounterKnown = false;
                stats[(1 - combo.Side, combo.Round)].CounterKnown = false;
            }
            else if (counterCandidates.Contains((combo.Start, combo.Side)) &&
                !counterCandidates.Contains((combo.Start, 1 - combo.Side)))
            {
                row.CounterHits++;
                stats[(1 - combo.Side, combo.Round)].TakenCounterHits++;
                events.Add(new(combo.Start, combo.Round, combo.Side, "康特",
                    $"对手先前动作 {defenderBefore.Action}",
                    "首击前对手处于攻击动作且原生康特易伤位有效，排除黄砂和互康"));
            }
        }
        var ddStartsByRound = new Dictionary<(int round, int side), List<int>>();
        foreach (var frame in frames)
        {
            if (frame.MatchState != 2 || frame.Round < 0) continue;
            for (var side = 0; side < 2; side++)
            {
                if (!BorderEscape(frame.Side(side).Action) || frame.Ordinal > 0 &&
                    BorderEscape(frames[frame.Ordinal - 1].Side(side).Action)) continue;
                var key = (frame.Round, side);
                if (!ddStartsByRound.TryGetValue(key, out var starts))
                    ddStartsByRound[key] = starts = [];
                starts.Add(frame.Ordinal);
            }
        }
        var impactsByRound = new Dictionary<(int round, int side), int[]>();
        foreach (var group in combos.GroupBy(c => (c.Round, c.Side)))
            impactsByRound[group.Key] = group.SelectMany(c => c.Impacts)
                .OrderBy(ordinal => ordinal).ToArray();
        foreach (var pressure in pressures)
        {
            var attacker = stats[(pressure.Attacker, pressure.Round)];
            var defender = stats[(pressure.Defender, pressure.Round)];
            var reversed = pressure.EndReason == "被反击" && events.Any(e =>
                e.Kind == "被凹" && e.Side == pressure.Attacker &&
                e.Ordinal >= pressure.Start && e.Ordinal <= pressure.End);
            var confirmedPunish = pressure.EndReason == "被反击" && !reversed &&
                events.Any(e => e.Kind is "技能硬直被攻击" or "技能前摇被攻击" &&
                    e.Side == pressure.Attacker && e.Ordinal >= pressure.Start &&
                    e.Ordinal <= pressure.End);
            if (reversed)
            {
                attacker.PressureReversed++;
                events.Add(new(pressure.Start, pressure.Round, pressure.Attacker,
                    "压制被凹", pressure.EndReason, "压制末端与被凹事件关联"));
            }
            else if (pressure.EndReason == "被反击" || confirmedPunish)
            {
                attacker.PressureInterrupted++;
                events.Add(new(pressure.Start, pressure.Round, pressure.Attacker,
                    "压制被打断", pressure.EndReason, "压制末端被对手确认命中"));
            }
            if (pressure.EndReason == "抓乱动")
            {
                attacker.PressureMashCatches++;
                defender.CaughtMashing++;
                events.Add(new(pressure.Start, pressure.Round, pressure.Attacker,
                    "压制抓乱动", pressure.EndReason, "压制末端对手非防御动作被命中"));
                events.Add(new(pressure.Start, pressure.Round, pressure.Defender,
                    "被抓乱动", pressure.EndReason, "自身非防御动作被确认命中"));
            }
            var caughtDd = false;
            if (ddStartsByRound.TryGetValue((pressure.Round, pressure.Defender), out var ddStarts) &&
                impactsByRound.TryGetValue((pressure.Round, pressure.Attacker), out var impacts))
            {
                var first = ddStarts.BinarySearch(pressure.Start);
                if (first < 0) first = ~first;
                for (var i = first; i < ddStarts.Count &&
                     ddStarts[i] <= pressure.End + 30; i++)
                {
                    var impact = Array.BinarySearch(impacts, ddStarts[i]);
                    if (impact < 0) impact = ~impact;
                    if (impact < impacts.Length && impacts[impact] <= ddStarts[i] + 20)
                    { caughtDd = true; break; }
                }
            }
            if (caughtDd)
            {
                attacker.PressureDdCatches++;
                events.Add(new(pressure.Start, pressure.Round, pressure.Attacker,
                    "压制抓DD", pressure.EndReason, "对手 DD 发动后 20 帧内确认命中"));
            }
        }
        var cardEvents = CardTimeline.Build(frames);
        foreach (var card in cardEvents.Where(x => x.CardId >= 0))
        {
            var kind = card.Kind switch {
                CardKind.Draw => "抽牌", CardKind.Observed => "首次观察牌",
                CardKind.Use => "用卡", CardKind.Cost => "符卡代价",
                _ => "卡牌变化"
            };
            events.Add(new ReplayEvent(card.Ordinal, card.Round, card.Side, kind,
                CardTimeline.Name(card),
                "手牌与已用牌队列的顺序变化；首次观察不等同该帧抽牌"));
        }
        var techChaseCandidates = new List<(int Round, int Side, int Start, int Free)>();
        foreach (var group in frames.Where(x => x.MatchState == 2 && x.Round >= 0)
                     .GroupBy(x => x.Round))
        {
            var roundFrames = group.ToArray();
            for (var side = 0; side < 2; side++)
            {
                if (!stats.TryGetValue((side, group.Key), out var row)) continue;
                var (total, effective) = InputEfficiency(roundFrames, side);
                row.Inputs = total;
                row.EffectiveInputs = effective;
                row.RedundantInputs = total - effective;
                row.LandingCancelsKnown = roundFrames.All(f => f.Side(side).FrameFlags >= 0);
                int? pendingLanding = null;
                for (var i = 1; i < roundFrames.Length; i++)
                {
                    var fighter = roundFrames[i].Side(side);
                    var old = roundFrames[i - 1].Side(side);
                    if (old.Y > .1 && fighter.Y <= .1 && old.FrameFlags >= 0 &&
                        (old.FrameFlags & 0x10) == 0 &&
                        (old.Action is >= 197 and <= 226 or >= 300 and < 620 || Hit(old.Action)))
                        pendingLanding = roundFrames[i].Ordinal;
                    if (pendingLanding is int landing)
                    {
                        if (roundFrames[i].Ordinal - landing > 3 || fighter.Y > .1)
                            pendingLanding = null;
                        else if (fighter.FrameFlags >= 0 && (fighter.FrameFlags & 0x10) != 0 &&
                                 !Hit(fighter.Action) && !Guard(fighter.Action) &&
                                 fighter.Action is not (97 or 98 or 99))
                        {
                            row.LandingCancels++;
                            events.Add(new(landing, group.Key, side, "落地取消",
                                $"落地后 {roundFrames[i].Ordinal - landing} 帧恢复可防御",
                                "空中不可防御硬直在落地后 3 帧内转为可防御状态"));
                            pendingLanding = null;
                        }
                    }
                    var started = fighter.Action != old.Action ||
                        fighter.ActionFrame < old.ActionFrame && fighter.ActionFrame <= 2;
                    if (started)
                    {
                        var high = fighter.Action switch {
                            208 => "28", 209 => "29", 210 => "27", _ => null
                        };
                        var small = fighter.Action switch {
                            6 => "8", 7 => "9", 8 => "7", _ => null
                        };
                        if (high is not null)
                            row.HighJumps[high] = row.HighJumps.GetValueOrDefault(high) + 1;
                        if (small is not null)
                            row.SmallJumps[small] = row.SmallJumps.GetValueOrDefault(small) + 1;
                    }
                    if ((fighter.Input & 0x80) == 0 || (old.Input & 0x80) != 0) continue;
                    var forward = fighter.Direction == 1 ? 0x08 : 0x04;
                    var backward = fighter.Direction == 1 ? 0x04 : 0x08;
                    var horizontal = (fighter.Input & forward) != 0 ? 1 :
                        (fighter.Input & backward) != 0 ? -1 : 0;
                    var vertical = (fighter.Input & 0x01) != 0 ? 1 :
                        (fighter.Input & 0x02) != 0 ? -1 : 0;
                    var direction = (horizontal, vertical) switch {
                        (1, 1) => "9", (0, 1) => "8", (-1, 1) => "7",
                        (1, 0) => "6", (-1, 0) => "4",
                        (1, -1) => "3", (0, -1) => "2", (-1, -1) => "1",
                        _ => null
                    };
                    if (direction is not null)
                        row.DDirections[direction] = row.DDirections.GetValueOrDefault(direction) + 1;
                }
                var attackStarts = roundFrames.Skip(1).Where((f, index) =>
                    f.Side(side).Action is >= 300 and < 620 &&
                    (f.Side(side).Action != roundFrames[index].Side(side).Action ||
                     f.Side(side).ActionFrame < roundFrames[index].Side(side).ActionFrame &&
                     f.Side(side).ActionFrame <= 2)).Select(f => f.Ordinal).ToArray();
                var wakeups = Wakeups(roundFrames, 1 - side);
                var ownImpactsForWakeup = combos.Where(c => c.Round == group.Key && c.Side == side)
                    .SelectMany(c => c.Impacts).ToArray();
                var enemyImpactsForWakeup = combos.Where(c => c.Round == group.Key && c.Side == 1 - side)
                    .SelectMany(c => c.Impacts).ToArray();
                foreach (var wakeup in wakeups)
                {
                    if (!attackStarts.Any(x => x >= wakeup - 30 && x <= wakeup + 60)) continue;
                    row.WakeupPressures++;
                    var ownHit = ownImpactsForWakeup.Where(x => x >= wakeup && x <= wakeup + 60)
                        .DefaultIfEmpty(int.MaxValue).Min();
                    var guardContact = pressures.Where(p => p.Round == group.Key &&
                            p.Attacker == side && p.Start >= wakeup && p.Start <= wakeup + 60)
                        .Select(p => p.Start).DefaultIfEmpty(int.MaxValue).Min();
                    var ownContact = Math.Min(ownHit, guardContact);
                    var enemyHit = enemyImpactsForWakeup.Where(x => x >= wakeup && x <= wakeup + 60)
                        .DefaultIfEmpty(int.MaxValue).Min();
                    if (ownContact != int.MaxValue && ownContact <= enemyHit)
                        row.WakeupPressureSuccess++;
                    else if (enemyHit != int.MaxValue)
                    {
                        row.WakeupPressureInterrupted++;
                        if (events.Any(e => e.Kind == "被凹" && e.Side == side &&
                            e.Ordinal >= wakeup && e.Ordinal <= wakeup + 60))
                            row.WakeupPressureReversed++;
                    }
                    events.Add(new(wakeup, group.Key, side, "压起身",
                        $"首次己方接触 {ownContact}，对方命中 {enemyHit}",
                        "对手起身前 30 帧至起身后 60 帧启动攻击"));
                }
                row.ReversalsKnown = roundFrames.Any(f => f.Side(side).FrameFlags >= 0);
                if (row.ReversalsKnown)
                {
                    var lastDefense = -1000;
                    var ownImpacts = combos.Where(c => c.Round == group.Key && c.Side == side)
                        .SelectMany(c => c.Impacts).ToArray();
                    for (var i = 0; i < roundFrames.Length; i++)
                    {
                        var fighter = roundFrames[i].Side(side);
                        if (Guard(fighter.Action) || Hit(fighter.Action) ||
                            fighter.Action is 97 or 98 or 99)
                            lastDefense = roundFrames[i].Ordinal;
                        if (i == 0 || fighter.Action is not (>= 300 and < 620) ||
                            roundFrames[i].Ordinal - lastDefense > 45 ||
                            !attackStarts.Contains(roundFrames[i].Ordinal)) continue;
                        var invincible = roundFrames.Skip(i).Take(15).Any(f =>
                            f.Side(side).Action == fighter.Action &&
                            f.Side(side).FrameFlags >= 0 &&
                            (f.Side(side).FrameFlags & 0x7000) != 0);
                        if (!invincible) continue;
                        row.ReversalsUsed++;
                        var hit = ownImpacts.Any(x => x >= roundFrames[i].Ordinal &&
                            x <= roundFrames[i].Ordinal + 60);
                        if (hit) row.ReversalsHit++;
                        events.Add(new(roundFrames[i].Ordinal, group.Key, side,
                            hit ? "凹成功" : "凹尝试", $"动作 {fighter.Action}",
                            "防御或受击后 45 帧内启动，并在 15 帧内确认无敌位"));
                    }
                }
                for (var i = 1; i < roundFrames.Length; i++)
                {
                    var current = roundFrames[i].Side(1 - side);
                    var previousFighter = roundFrames[i - 1].Side(1 - side);
                    if (!Tech(current.Action) || Tech(previousFighter.Action)) continue;
                    var start = roundFrames[i].Ordinal;
                    var free = start + 60;
                    for (var j = i + 1; j < roundFrames.Length &&
                         roundFrames[j].Ordinal <= start + 60; j++)
                    {
                        var later = roundFrames[j].Side(1 - side);
                        if (!Tech(later.Action) && later.HitStop == 0 && Actionable(later.Action))
                        { free = roundFrames[j].Ordinal; break; }
                    }
                    if (!attackStarts.Any(x => x >= start - 15 && x <= free)) continue;
                    techChaseCandidates.Add((group.Key, side, start, free));
                }
                var enemyStarts = combos.Where(x => x.Round == group.Key && x.Side == 1 - side)
                    .Select(x => x.Start).OrderBy(x => x).ToArray();
                foreach (var wakeup in wakeups)
                {
                    var caught = enemyStarts.FirstOrDefault(x => x >= wakeup && x <= wakeup + 60, -1);
                    if (caught < 0) continue;
                    row.WakeupPressureFailures++;
                    events.Add(new(wakeup, group.Key, side, "压起身失败",
                        $"起身 {wakeup} → 受击 {caught}",
                        "对手起身后 60 帧内确认对自身造成命中伤害"));
                }
            }
        }
        var records = new Dictionary<int, List<Frame>>();
        int? recordRound = null;
        foreach (var frame in frames)
        {
            if (frame.MatchState == 2 && frame.Round >= 0)
                recordRound = frame.Round;
            else if (frame.MatchState is not (3 or 5)) continue;
            if (recordRound is int round)
            {
                if (!records.TryGetValue(round, out var values))
                    records[round] = values = [];
                values.Add(frame);
            }
        }
        foreach (var (round, values) in records)
            for (var side = 0; side < 2; side++)
            {
                if (!stats.TryGetValue((side, round), out var row)) continue;
                int? pendingTech = null;
                Frame? old = null;
                foreach (var frame in values)
                {
                    var fighter = frame.Side(side);
                    var opponent = frame.Side(1 - side);
                    var previousFighter = old?.Side(side);
                    var previousOpponent = old?.Side(1 - side);
                    if (Tech(fighter.Action) &&
                        (previousFighter is null || !Tech(previousFighter.Action)))
                        pendingTech = frame.Ordinal;
                    if (pendingTech is int techStart)
                    {
                        var damaged = previousFighter is not null &&
                            previousOpponent is not null &&
                            fighter.Hp < previousFighter.Hp && !Guard(fighter.Action) &&
                            (Hit(fighter.Action) || opponent.ComboDamage > 0 &&
                             (opponent.ComboCount != previousOpponent.ComboCount ||
                              opponent.ComboDamage != previousOpponent.ComboDamage));
                        if (damaged)
                        {
                            row.TechTrapped++;
                            events.Add(new(techStart, round, side,
                                "受身未自由行动被攻击", $"受身 {techStart} → 受击 {frame.Ordinal}",
                                "受身后尚未恢复自由行动，已确认实际命中"));
                            pendingTech = null;
                        }
                        else if (!Tech(fighter.Action) && fighter.HitStop == 0 &&
                                 Actionable(fighter.Action)) pendingTech = null;
                    }
                    old = frame;
                }
            }
        foreach (var (round, side, start, free) in techChaseCandidates)
        {
            var row = stats[(side, round)];
            row.TechChases++;
            var success = events.Any(e => e.Kind == "受身未自由行动被攻击" &&
                e.Side == 1 - side && e.Ordinal == start);
            if (success) row.TechChaseSuccess++;
            events.Add(new(start, round, side, success ? "抓受身成功" : "抓受身尝试",
                $"受身开始 {start}，自由行动边界 {free}",
                success ? "受身期间开始追击并确认受击" : "受身附近已开始攻击，但未确认追击命中"));
        }
        var escapeAttempts = EscapeAttemptDetector.Detect(frames, pressures);
        foreach (var attempt in escapeAttempts)
        {
            if (!stats.TryGetValue((attempt.Defender, attempt.Round), out var row)) continue;
            row.EscapeAttempts++;
            if (attempt.Outcome == "成功") row.EscapeSuccess++;
            else if (attempt.Outcome == "失败") row.EscapeFailed++;
            else row.EscapeUnresolved++;
            events.Add(new(attempt.Start, attempt.Round, attempt.Defender,
                "逃压" + attempt.Outcome, $"压制起点 {attempt.PressureStart} · 至 {attempt.End}",
                "防御期间或相邻空隙内的一组输入；需对应动作及压制结束证据，恢复后观察 30 帧"));
        }
        foreach (var row in stats.Values)
            if (stats.TryGetValue((1 - row.Side, row.Round), out var other))
            {
                row.TakenChipDamage = other.ChipDamage;
                row.TakenWeatherDamage = other.WeatherDamage;
                row.TakenTyphoonDamage = other.TyphoonDamage;
            }
        return new ReplaySession {
            Source = source, Header = header, Frames = frames, TraceOrigin = traceOrigin,
            Events = events.OrderBy(x => x.Ordinal).ThenBy(x => x.Side).ToArray(),
            Combos = combos, Pressures = pressures, EscapeAttempts = escapeAttempts,
            PressureGaps = PressureGapDetector.Detect(frames), Curves = curves,
            CardEvents = cardEvents,
            Stats = stats.Values.OrderBy(x => x.Round).ThenBy(x => x.Side).ToArray()
        };

        void FinishPressure(int defender, int end, string reason,
            int? observedUntil = null)
        {
            if (livePressure[defender] is not { } p) return;
            var confirmed = p.Moves.Count(x => x >= 0);
            if (confirmed > 0)
            {
                var route = string.Join(" → ", p.Moves.Select(x =>
                    x < 0 ? "接触招式未确认" : x.ToString()));
                if (reason is not ("命中" or "抓dd" or "抓跳" or "抓乱动" or "确反"))
                {
                    var missed = recentAttack[p.Attacker].LastOrDefault(x =>
                        x.ordinal > p.LastContact && x.ordinal <= (observedUntil ?? end));
                    if (missed != default) route += $"  ↳ 未接上：{missed.action}";
                }
                pressures.Add(new Pressure(p.Start, end, p.Round, p.Attacker, p.Defender,
                    p.Contacts, confirmed, route, reason));
                var key = (p.Attacker, p.Round);
                if (!stats.TryGetValue(key, out var row))
                    stats[key] = row = new SideStats { Side = p.Attacker, Round = p.Round };
                row.Pressures++;
            }
            livePressure[defender] = null;
        }
    }

    private static (int Total, int Effective) InputEfficiency(IReadOnlyList<Frame> frames, int side)
    {
        var fighters = frames.Select(x => x.Side(side)).ToArray();
        var masks = fighters.Select(x => x.Input & 0x3ff).ToArray();
        var total = 0;
        var effective = 0;
        for (var i = 0; i < fighters.Length; i++)
        {
            var presses = masks[i] & ~(i == 0 ? 0 : masks[i - 1]);
            for (var bit = 0; bit < 10; bit++)
            {
                var flag = 1 << bit;
                if ((presses & flag) == 0) continue;
                total++;
                var release = i + 1;
                while (release < fighters.Length && (masks[release] & flag) != 0) release++;
                var end = Math.Min(fighters.Length, Math.Max(release, i + 20));
                for (var j = i; j < end; j++)
                {
                    var current = fighters[j];
                    var earlier = j == 0 ? null : fighters[j - 1];
                    var started = earlier is not null && (current.Action != earlier.Action ||
                        current.ActionFrame < earlier.ActionFrame && current.ActionFrame <= 2);
                    var within20 = j - i <= 20;
                    var useful = bit < 4
                        ? j < release && Guard(current.Action) &&
                          (bit == 0 || bit == 1 || bit == (current.Direction == 1 ? 2 : 3)) ||
                          j < release && earlier is not null &&
                          Math.Abs(current.X - earlier.X) + Math.Abs(current.Y - earlier.Y) > .05 &&
                          (current.Action <= 10 || current.Action is >= 197 and <= 226) ||
                          started && within20 && (current.Action is >= 200 and <= 226 || current.Action >= 300)
                        : bit is 4 or 5 or 6
                            ? started && within20 && current.Action >= 300
                            : bit == 7
                                ? started && within20 && current.Action is >= 197 and <= 226
                                : earlier is not null && within20 &&
                                  (current.CardCount != earlier.CardCount || started && current.Action >= 600);
                    if (!useful) continue;
                    effective++;
                    break;
                }
            }
        }
        return (total, effective);
    }

    private static IReadOnlyList<int> Wakeups(IReadOnlyList<Frame> frames, int side)
    {
        var down = false;
        var result = new List<int>();
        foreach (var frame in frames)
        {
            var fighter = frame.Side(side);
            if (fighter.Action is >= 97 and <= 99) down = true;
            else if (down && fighter.HitStop == 0 &&
                (fighter.Action <= 10 || fighter.Action is 180 or 181 ||
                 fighter.Action is >= 197 and <= 226 || fighter.Action >= 300))
            {
                result.Add(frame.Ordinal);
                down = false;
            }
        }
        return result;
    }

    internal static string BoardPosition(Fighter fighter)
    {
        if (fighter.Direction is not (1 or -1)) return "位置未知";
        var front = fighter.Direction == 1 ? 1280 - fighter.X : fighter.X;
        if (front <= 160) return "前板边";
        if (front <= 360) return "前近板";
        if (front >= 1120) return "背板边";
        if (front >= 920) return "背近板";
        return "板中";
    }

    private sealed class PressureBuilder(int start, int round, int attacker, int defender)
    {
        public int Start = start, LastContact = start, Round = round, Attacker = attacker,
            Defender = defender, Contacts, FreeFrames;
        public int? DefenderDdOrdinal, AttackerGuardOrdinal;
        public int? DefenderJumpOrdinal, FreeStart;
        public List<int> Moves = [];
    }
}

public static class SessionLoader
{
    // Trial verdicts only inspect engine frames, pressure gaps and card use.
    // Full combat statistics are built when a branch is opened for review.
    public static ReplaySession LoadSimulationTrace(string path, ReplayHeader header,
        string tracePath, bool detectGaps = false,
        IReadOnlyList<Frame>? baselinePrefix = null, int firstChangedFrame = 0,
        bool tailOnly = false)
    {
        var frames = baselinePrefix is null ? TraceReader.Read(tracePath) :
            tailOnly
                ? TraceReader.ReadTailWithPrefix(tracePath, baselinePrefix, firstChangedFrame)
                : TraceReader.ReadWithPrefix(tracePath, baselinePrefix, firstChangedFrame);
        var cardStart = baselinePrefix is null ? 0 : Math.Max(0, firstChangedFrame - 1);
        return new ReplaySession {
            Source = path, Header = header, Frames = frames,
            TraceOrigin = "引擎推演轨迹",
            Events = [], Combos = [], Pressures = [], EscapeAttempts = [],
            PressureGaps = detectGaps ? PressureGapDetector.Detect(frames) : [],
            Curves = [], CardEvents = CardTimeline.Build(frames.Skip(cardStart).ToArray()), Stats = []
        };
    }

    public static ReplaySession Load(string path, string root, string? traceOverride = null,
        ReplayHeader? preloadedHeader = null)
    {
        if (!string.Equals(Path.GetExtension(path), ".rep", StringComparison.OrdinalIgnoreCase))
            return ReplayAnalyzer.Analyze(path, null, TraceReader.Read(path), "导入的 CSV");
        var header = preloadedHeader is not null &&
            string.Equals(Path.GetFullPath(preloadedHeader.Path), Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase)
            ? preloadedHeader : ReplayHeader.Read(path);
        if (traceOverride is not null)
            return ReplayAnalyzer.Analyze(path, header, TraceReader.Read(traceOverride),
                "当前探针轨迹");
        var verifiedTrace = Path.Combine(root, "verified_trace_" + header.Hash + ".csv");
        if (File.Exists(verifiedTrace))
        {
            try
            {
                var verifiedFrames = TraceReader.Read(verifiedTrace);
                if (verifiedFrames.Count >= 100 && verifiedFrames[^1].MatchState == 5)
                    return ReplayAnalyzer.Analyze(path, header, verifiedFrames,
                        "已验证的完整实机轨迹");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or FormatException)
            { /* A damaged saved trace should not prevent importing the REP. */ }
        }
        var nativeTrace = Path.Combine(root, "trace_" + header.Hash + ".csv");
        List<Frame>? nativeFramesCache = null;
        if (File.Exists(nativeTrace))
        {
            try
            {
                nativeFramesCache = TraceReader.Read(nativeTrace);
                if (nativeFramesCache.Count >= 100 && nativeFramesCache[^1].MatchState == 5)
                    return ReplayAnalyzer.Analyze(path, header, nativeFramesCache,
                        "本机完整实机轨迹");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or FormatException)
            { /* An incomplete native trace can still be replaced by a historical one. */ }
        }
        var candidates = new List<string>();
        if (File.Exists(nativeTrace)) candidates.Add(nativeTrace);
        var cache = Path.Combine(root, "v1.0", "runtime", "cache");
        if (Directory.Exists(cache))
            candidates.AddRange(Directory.EnumerateFiles(cache, header.Hash + "_v*.csv")
                .OrderByDescending(File.GetLastWriteTimeUtc));
        var stem = Path.GetFileNameWithoutExtension(path);
        var knownRep = Path.Combine(root, "v1.0", "tests", "manual_rep_2026-10-07",
            "reps", stem + ".rep");
        var sample = Path.Combine(root, "v1.0", "tests", "manual_rep_2026-10-07",
            "traces", stem + ".csv");
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(knownRep),
                StringComparison.OrdinalIgnoreCase) && File.Exists(sample))
            candidates.Add(sample);
        List<Frame>? best = null;
        string? bestPath = null;
        Exception? invalid = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var frames = nativeFramesCache is not null &&
                    string.Equals(candidate, nativeTrace, StringComparison.OrdinalIgnoreCase)
                    ? nativeFramesCache : TraceReader.Read(candidate);
                if (best is null || frames.Count > best.Count)
                {
                    best = frames;
                    bestPath = candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or FormatException)
            {
                invalid ??= ex;
            }
        }
        if (best is not null) return ReplayAnalyzer.Analyze(path, header, best,
            string.Equals(bestPath, nativeTrace, StringComparison.OrdinalIgnoreCase)
                ? "本机短程轨迹" : "历史缓存轨迹");
        if (invalid is not null) throw new InvalidDataException(
            "已找到该 REP 的轨迹，但所有候选轨迹都无法读取。", invalid);
        return ReplayAnalyzer.Analyze(path, header, [], "仅 REP 头");
    }
}
