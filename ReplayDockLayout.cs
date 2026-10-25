using System.Windows;

namespace SokuReplayLab;

public enum ReplayDockZone { Center, Left, Right, Top, Bottom }
public sealed record ReplayDockSlot(string Id, ReplayPanelLayout Bounds, string[] Panels, string? Active);
// A partition owns its boundary. Leaf rectangles are derived, never independently edited.
public sealed record ReplayDockPartition(string? Slot, bool Across = false, double Ratio = .5,
    ReplayDockPartition? First = null, ReplayDockPartition? Second = null, double DefaultRatio = .5);
public sealed record ReplayDockReturn(string Anchor, ReplayDockZone Zone, int Index = 0);
public sealed record ReplayDockState(int Format, double Width, double Height,
    ReplayDockSlot[] Slots, Dictionary<string, string> Homes, string Name = "当前布局", ReplayDockPartition? Partition = null,
    Dictionary<string, ReplayDockReturn>? Returns = null);

public sealed class ReplayDockLayout
{
    private readonly Dictionary<string, Size> minimums;
    private readonly Stack<ReplayDockState> undo = new(), redo = new();
    public ReplayDockState State { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public long Revision { get; private set; }
    public void Rename(string name) => State = State with { Name = name };
    public ReplayDockLayout(ReplayDockState initial, IReadOnlyDictionary<string, Size> minimums)
    {
        this.minimums = minimums.ToDictionary(x => x.Key, x => x.Value);
        State = Clean(initial) ?? throw new ArgumentException("布局不可用。");
    }
    public static ReplayDockState Copy(ReplayDockState value) => value with {
        Slots = (value.Slots ?? []).Where(s => s is not null).Select(s => s with { Panels = (s.Panels ?? []).ToArray() }).ToArray(),
        Homes = new(value.Homes ?? [], StringComparer.Ordinal), Returns = new(value.Returns ?? [], StringComparer.Ordinal)
    };
    private ReplayDockState? Clean(ReplayDockState? value)
    {
        if (value is null || value.Format is not (1 or 2) || !double.IsFinite(value.Width) || !double.IsFinite(value.Height) ||
            value.Width is < 300 or > 20000 || value.Height is < 200 or > 20000 || value.Slots is not { Length: > 0 and <= 64 }) return null;
        var ids = new HashSet<string>(); var panels = new HashSet<string>(); var slots = new List<ReplayDockSlot>();
        foreach (var slot in value.Slots)
        {
            if (slot is null || string.IsNullOrWhiteSpace(slot.Id) || slot.Id.Length > 100 || !ids.Add(slot.Id) || slot.Bounds is null) return null;
            var b = slot.Bounds;
            if (!new[] { b.X, b.Y, b.Width, b.Height }.All(double.IsFinite) || b.X < 0 || b.Y < 0 || b.Width <= 0 || b.Height <= 0 ||
                b.X + b.Width > 1.001 || b.Y + b.Height > 1.001) return null;
            var known = (slot.Panels ?? []).Where(p => p is not null && minimums.ContainsKey(p) && panels.Add(p)).ToArray();
            slots.Add(slot with { Panels = known, Active = known.Contains(slot.Active) ? slot.Active : known.FirstOrDefault() });
        }
        var root = value.Partition;
        if (root is null && value.Format == 1) root = ImportRegions(slots);
        var leaves = new HashSet<string>();
        if (root is null || !Valid(root, leaves, 0) || !leaves.SetEquals(ids)) return null;
        // Older layouts retained an empty region after every move/close.
        // Collapse those regions when loading as well as during new edits.
        foreach (var empty in slots.Where(s => s.Panels.Length == 0).ToArray())
        {
            if (slots.Count == 1) break;
            root = Remove(root, empty.Id)!; slots.Remove(empty); ids.Remove(empty.Id);
        }
        var homes = (value.Homes ?? []).Where(x => minimums.ContainsKey(x.Key) && ids.Contains(x.Value)).ToDictionary(x => x.Key, x => x.Value);
        foreach (var slot in slots) foreach (var panel in slot.Panels) homes[panel] = slot.Id;
        var name = value.Name ?? "当前布局";
        var returns = (value.Returns ?? []).Where(p => minimums.ContainsKey(p.Key) && p.Value is not null && p.Value.Anchor is not null &&
            minimums.ContainsKey(p.Value.Anchor) && p.Key != p.Value.Anchor && Enum.IsDefined(p.Value.Zone) && p.Value.Index is >= 0 and <= 64)
            .ToDictionary(p => p.Key, p => p.Value);
        var projected = Project(value with { Format = 2, Slots = slots.ToArray(), Homes = homes, Returns = returns,
            Name = name[..Math.Min(name.Length, 60)], Partition = root });
        return projected.Width <= 20000 && projected.Height <= 20000 ? projected : null;
    }
    private static bool Valid(ReplayDockPartition n, HashSet<string> leaves, int depth)
    {
        if (depth > 64) return false;
        if (n.Slot is not null) return n.First is null && n.Second is null && leaves.Add(n.Slot);
        return double.IsFinite(n.Ratio) && n.Ratio is > 0 and < 1 && double.IsFinite(n.DefaultRatio) && n.DefaultRatio is > 0 and < 1 &&
            n.First is not null && n.Second is not null && Valid(n.First, leaves, depth + 1) && Valid(n.Second, leaves, depth + 1);
    }
    // Import only the existing spatial arrangement, retaining columns and vertical order.
    private static ReplayDockPartition ImportRegions(IReadOnlyList<ReplayDockSlot> slots)
    {
        if (slots.Count == 1) return new(slots[0].Id);
        foreach (var across in new[] { true, false })
        {
            var ordered = slots.OrderBy(s => across ? s.Bounds.X : s.Bounds.Y).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                var end = ordered.Take(i).Max(s => across ? s.Bounds.X + s.Bounds.Width : s.Bounds.Y + s.Bounds.Height);
                var start = across ? ordered[i].Bounds.X : ordered[i].Bounds.Y;
                if (end > start + .002) continue;
                var low = slots.Min(s => across ? s.Bounds.X : s.Bounds.Y);
                var high = slots.Max(s => across ? s.Bounds.X + s.Bounds.Width : s.Bounds.Y + s.Bounds.Height);
                var ratio = Math.Clamp(((start + end) / 2 - low) / (high - low), .02, .98);
                return new(null, across, ratio, ImportRegions(ordered[..i]), ImportRegions(ordered[i..]), ratio);
            }
        }
        var fallback = slots.OrderBy(s => s.Bounds.Y).ThenBy(s => s.Bounds.X).ToArray();
        return new(null, false, 1d / slots.Count, new(fallback[0].Id), ImportRegions(fallback[1..]), 1d / slots.Count);
    }
    private static ReplayDockPartition Replace(ReplayDockPartition n, string id, ReplayDockPartition replacement) =>
        n.Slot == id ? replacement : n.Slot is not null ? n : n with { First = Replace(n.First!, id, replacement), Second = Replace(n.Second!, id, replacement) };
    private static ReplayDockPartition? Remove(ReplayDockPartition n, string id)
    {
        if (n.Slot is not null) return n.Slot == id ? null : n;
        var a = Remove(n.First!, id); var b = Remove(n.Second!, id);
        return a is null ? b : b is null ? a : n with { First = a, Second = b };
    }
    private static bool Contains(ReplayDockPartition n, string id) => n.Slot == id || n.Slot is null && (Contains(n.First!, id) || Contains(n.Second!, id));
    public Size Minimum(ReplayDockSlot slot) => new(Math.Max(220, slot.Panels.Select(p => minimums[p].Width).DefaultIfEmpty(220).Max()),
        Math.Max(120, slot.Panels.Select(p => minimums[p].Height).DefaultIfEmpty(120).Max()));
    private Size Required(ReplayDockPartition n, ReplayDockState state)
    {
        if (n.Slot is not null) return Minimum(state.Slots.Single(s => s.Id == n.Slot));
        var a = Required(n.First!, state); var b = Required(n.Second!, state);
        return n.Across ? new(a.Width + b.Width + 8, Math.Max(a.Height, b.Height))
            : new(Math.Max(a.Width, b.Width), a.Height + b.Height + 8);
    }
    public Size RequiredSize(ReplayDockState? state = null) => Required((state ?? State).Partition!, state ?? State);
    private ReplayDockState Project(ReplayDockState state)
    {
        var required = RequiredSize(state);
        state = state with { Width = Math.Max(state.Width, required.Width), Height = Math.Max(state.Height, required.Height) };
        var rectangles = new Dictionary<string, ReplayPanelLayout>();
        void Place(ReplayDockPartition n, ReplayPanelLayout b)
        {
            if (n.Slot is not null) { rectangles[n.Slot] = b; return; }
            var gap = 8 / (n.Across ? state.Width : state.Height);
            var length = (n.Across ? b.Width : b.Height) - gap;
            var a = Required(n.First!, state); var c = Required(n.Second!, state);
            var scale = n.Across ? state.Width : state.Height;
            var lower = (n.Across ? a.Width : a.Height) / scale;
            var upper = length - (n.Across ? c.Width : c.Height) / scale;
            var first = Math.Clamp(length * n.Ratio, lower, Math.Max(lower, upper));
            Place(n.First!, n.Across ? b with { Width = first } : b with { Height = first });
            Place(n.Second!, n.Across ? b with { X = b.X + first + gap, Width = length - first }
                : b with { Y = b.Y + first + gap, Height = length - first });
        }
        Place(state.Partition!, new(0, 0, 1, 1));
        return state with { Slots = state.Slots.Select(s => s with { Bounds = rectangles[s.Id] }).ToArray() };
    }
    public void Arrange(double width, double height) => State = Project(State with { Width = Math.Clamp(width, 300, 20000), Height = Math.Clamp(height, 200, 20000) });
    public bool AtSize(double width, double height, Func<bool> action)
    {
        var before = State; Arrange(width, height); var success = false;
        try { success = action(); return success; } finally { if (!success) State = before; }
    }
    private static bool Equivalent(ReplayDockState a, ReplayDockState b) => a.Name == b.Name && a.Partition == b.Partition && a.Slots.Length == b.Slots.Length &&
        (a.Returns?.Count ?? 0) == (b.Returns?.Count ?? 0) && (a.Returns ?? []).All(p => b.Returns?.GetValueOrDefault(p.Key) == p.Value) &&
        a.Homes.Count == b.Homes.Count && a.Homes.All(p => b.Homes.GetValueOrDefault(p.Key) == p.Value) && a.Slots.Zip(b.Slots).All(p =>
            p.First.Id == p.Second.Id && p.First.Active == p.Second.Active && p.First.Panels.SequenceEqual(p.Second.Panels));
    private bool Commit(ReplayDockState next)
    {
        var clean = Clean(next); if (clean is null || Equivalent(State, clean)) return false;
        RememberUndo(); redo.Clear(); State = Copy(clean); Revision++; return true;
    }
    public bool Restore(ReplayDockState? saved, bool remember = false)
    {
        var clean = Clean(saved); if (clean is null) return false;
        if (remember && Equivalent(State, clean)) return true;
        if (remember) RememberUndo(); else undo.Clear();
        redo.Clear(); State = Copy(clean); Revision++; return true;
    }
    public bool Activate(string slotId, string panel)
    {
        var slot = State.Slots.FirstOrDefault(s => s.Id == slotId);
        if (slot is null || !slot.Panels.Contains(panel) || slot.Active == panel) return false;
        State = State with { Slots = State.Slots.Select(s => s.Id == slotId ? s with { Active = panel } : s).ToArray() }; Revision++; return true;
    }
    public void RestoreSelection(ReplayDockState before)
    {
        State = State with { Slots = State.Slots.Select(s => {
            var old = before.Slots.FirstOrDefault(o => o.Id == s.Id);
            return old is not null && s.Panels.Contains(old.Active) ? s with { Active = old.Active } : s;
        }).ToArray() }; Revision++;
    }
    public bool Open(string panel, string? preferred = null)
    {
        if (!minimums.ContainsKey(panel)) return false;
        var current = State.Slots.FirstOrDefault(s => s.Panels.Contains(panel));
        if (current is not null && preferred is null) return Activate(current.Id, panel);
        if (current is null && preferred is null && State.Returns?.GetValueOrDefault(panel) is { } destination)
        {
            var anchor = destination.Anchor; var visited = new HashSet<string> { panel };
            while (visited.Add(anchor))
            {
                var neighbour = State.Slots.FirstOrDefault(s => s.Panels.Contains(anchor));
                if (neighbour is not null) return MovePanel(panel, neighbour.Id, destination.Zone, destination.Index);
                if (State.Returns.GetValueOrDefault(anchor) is not { } next) break;
                anchor = next.Anchor;
            }
        }
        var home = preferred ?? State.Homes.GetValueOrDefault(panel);
        return MovePanel(panel, (State.Slots.FirstOrDefault(s => s.Id == home) ?? State.Slots[0]).Id, ReplayDockZone.Center);
    }
    private static ReplayDockSlot[] RemovePanel(IEnumerable<ReplayDockSlot> slots, string panel) => slots.Select(s => {
        var list = s.Panels.Where(p => p != panel).ToArray();
        return s with { Panels = list, Active = list.Contains(s.Active) ? s.Active : list.FirstOrDefault() };
    }).ToArray();
    public bool Close(string panel)
    {
        var source = State.Slots.FirstOrDefault(s => s.Panels.Contains(panel));
        if (source is null) return false;
        var returns = new Dictionary<string, ReplayDockReturn>(State.Returns ?? []);
        if (source.Panels.FirstOrDefault(p => p != panel) is { } tab)
            returns[panel] = new(tab, ReplayDockZone.Center, Array.IndexOf(source.Panels, panel));
        else
        {
            ReplayDockReturn? FindNeighbour(ReplayDockPartition node)
            {
                if (node.Slot is not null) return null;
                var first = node.First!.Slot == source.Id;
                if (first || node.Second!.Slot == source.Id)
                {
                    var sibling = first ? node.Second! : node.First!;
                    var anchor = State.Slots.Where(s => Contains(sibling, s.Id)).SelectMany(s => s.Panels).FirstOrDefault();
                    return anchor is null ? null : new(anchor, node.Across
                        ? first ? ReplayDockZone.Left : ReplayDockZone.Right
                        : first ? ReplayDockZone.Top : ReplayDockZone.Bottom);
                }
                return FindNeighbour(node.First!) ?? FindNeighbour(node.Second!);
            }
            if (FindNeighbour(State.Partition!) is { } neighbour) returns[panel] = neighbour;
        }
        var slots = RemovePanel(State.Slots, panel);
        var root = State.Partition;
        if (source.Panels.Length == 1 && slots.Length > 1)
        { slots = slots.Where(s => s.Id != source.Id).ToArray(); root = Remove(root!, source.Id); }
        return Commit(State with { Slots = slots, Partition = root, Returns = returns });
    }
    private static ReplayDockPartition Insert(ReplayDockPartition root, string target, string incoming, ReplayDockZone zone, bool workspace = false)
    {
        var leading = zone is ReplayDockZone.Left or ReplayDockZone.Top;
        var existing = workspace ? root : new ReplayDockPartition(target);
        var added = new ReplayDockPartition(incoming);
        var split = new ReplayDockPartition(null, zone is ReplayDockZone.Left or ReplayDockZone.Right, .5,
            leading ? added : existing, leading ? existing : added);
        return workspace ? split : Replace(root, target, split);
    }
    public bool MovePanel(string panel, string targetId, ReplayDockZone zone, int? insertion = null, bool workspace = false)
    {
        if (!minimums.ContainsKey(panel) || !State.Slots.Any(s => s.Id == targetId)) return false;
        var source = State.Slots.FirstOrDefault(s => s.Panels.Contains(panel));
        if (source?.Id == targetId && source.Panels.Length == 1 && zone != ReplayDockZone.Center) return false;
        var slots = RemovePanel(State.Slots, panel).ToList(); var index = slots.FindIndex(s => s.Id == targetId);
        var target = slots[index]; var root = State.Partition!; var home = targetId;
        if (source is not null && source.Id != targetId && source.Panels.Length == 1)
        { slots.RemoveAll(s => s.Id == source.Id); root = Remove(root, source.Id)!; index = slots.FindIndex(s => s.Id == targetId); }
        if (zone == ReplayDockZone.Center)
        {
            var list = target.Panels.ToList(); list.Insert(Math.Clamp(insertion ?? list.Count, 0, list.Count), panel);
            slots[index] = target with { Panels = list.ToArray(), Active = panel };
        }
        else
        {
            home = "slot-" + Guid.NewGuid().ToString("N");
            slots.Add(new(home, target.Bounds, [panel], panel)); root = Insert(root, targetId, home, zone, workspace);
        }
        var homes = new Dictionary<string, string>(State.Homes) { [panel] = home };
        return Commit(State with { Slots = slots.ToArray(), Homes = homes, Partition = root });
    }
    public bool MoveSlot(string sourceId, string targetId, ReplayDockZone zone, bool workspace = false)
    {
        if (sourceId == targetId) return false;
        var source = State.Slots.FirstOrDefault(s => s.Id == sourceId); var target = State.Slots.FirstOrDefault(s => s.Id == targetId);
        if (source is null || target is null) return false;
        var root = Remove(State.Partition!, sourceId)!;
        var slots = State.Slots.Where(s => s.Id != sourceId).ToList(); var index = slots.FindIndex(s => s.Id == targetId);
        if (zone == ReplayDockZone.Center) slots[index] = target with { Panels = target.Panels.Concat(source.Panels).ToArray(), Active = source.Active ?? target.Active };
        else { root = Insert(root, targetId, sourceId, zone, workspace); slots.Add(source); }
        var homes = new Dictionary<string, string>(State.Homes);
        foreach (var panel in source.Panels) homes[panel] = zone == ReplayDockZone.Center ? targetId : sourceId;
        return Commit(State with { Slots = slots.ToArray(), Homes = homes, Partition = root });
    }
    public bool Delete(string slotId, string? moveTo = null)
    {
        var source = State.Slots.FirstOrDefault(s => s.Id == slotId);
        if (source is null || moveTo == slotId) return false;
        if (State.Slots.Length == 1) return Commit(State with { Slots = [source with { Panels = [], Active = null }] });
        var slots = State.Slots.Where(s => s.Id != slotId).ToList();
        if (moveTo is not null)
        {
            var index = slots.FindIndex(s => s.Id == moveTo); if (index < 0) return false;
            var target = slots[index]; slots[index] = target with { Panels = target.Panels.Concat(source.Panels).ToArray(), Active = source.Active ?? target.Active };
        }
        return Commit(State with { Slots = slots.ToArray(), Partition = Remove(State.Partition!, slotId) });
    }
    private ReplayDockPartition? Boundary(string first, string second, bool across)
    {
        ReplayDockPartition? Find(ReplayDockPartition n)
        {
            if (n.Slot is not null) return null;
            if (n.Across == across && Contains(n.First!, first) && Contains(n.Second!, second)) return n;
            return Find(n.First!) ?? Find(n.Second!);
        }
        return Find(State.Partition!);
    }
    private static ReplayDockPartition ReplaceNode(ReplayDockPartition n, ReplayDockPartition target, ReplayDockPartition value) =>
        ReferenceEquals(n, target) ? value : n.Slot is not null ? n : n with { First = ReplaceNode(n.First!, target, value), Second = ReplaceNode(n.Second!, target, value) };
    private ReplayPanelLayout Region(ReplayDockPartition n)
    {
        var slots = State.Slots.Where(s => Contains(n, s.Id)).Select(s => s.Bounds).ToArray();
        var x = slots.Min(s => s.X); var y = slots.Min(s => s.Y);
        return new(x, y, slots.Max(s => s.X + s.Width) - x, slots.Max(s => s.Y + s.Height) - y);
    }
    public bool ResizeBoundary(string firstId, string secondId, double change, bool horizontal)
    {
        if (!double.IsFinite(change)) return false;
        var node = Boundary(firstId, secondId, horizontal); if (node is null) return false;
        var b = Region(node); var scale = horizontal ? State.Width : State.Height;
        var length = (horizontal ? b.Width : b.Height) - 8 / scale;
        var a = Required(node.First!, State); var c = Required(node.Second!, State);
        var lower = (horizontal ? a.Width : a.Height) / (length * scale);
        var upper = 1 - (horizontal ? c.Width : c.Height) / (length * scale);
        if (lower > upper + .00000001) return false;
        var actual = Region(node.First!);
        var currentRatio = (horizontal ? actual.Width : actual.Height) / length;
        var ratio = Math.Clamp(currentRatio + change / length, lower, Math.Max(lower, upper));
        if (Math.Abs(ratio - node.Ratio) < .000000001) return false;
        return Commit(State with { Partition = ReplaceNode(State.Partition!, node, node with { Ratio = ratio }) });
    }
    public bool ResetBoundary(string first, string second, bool across)
    {
        var node = Boundary(first, second, across); if (node is null) return false;
        return Commit(State with { Partition = ReplaceNode(State.Partition!, node, node with { Ratio = node.DefaultRatio }) });
    }
    public IEnumerable<(string First, string Second, bool Across)> Boundaries()
    {
        string Leaf(ReplayDockPartition n) => n.Slot ?? Leaf(n.First!);
        IEnumerable<(string, string, bool)> Walk(ReplayDockPartition n)
        {
            if (n.Slot is not null) yield break;
            yield return (Leaf(n.First!), Leaf(n.Second!), n.Across);
            foreach (var b in Walk(n.First!)) yield return b;
            foreach (var b in Walk(n.Second!)) yield return b;
        }
        return Walk(State.Partition!);
    }
    public ReplayPanelLayout BoundaryBounds(string first, string second, bool across)
    {
        var n = Boundary(first, second, across)!; var b = Region(n); var a = Region(n.First!);
        var gap = 8 / (across ? State.Width : State.Height);
        return across ? new(a.X + a.Width, b.Y, gap, b.Height)
            : new(b.X, a.Y + a.Height, b.Width, gap);
    }
    public bool Undo() { if (!CanUndo) return false; redo.Push(Copy(State)); State = undo.Pop(); Revision++; return true; }
    public bool Redo() { if (!CanRedo) return false; RememberUndo(); State = redo.Pop(); Revision++; return true; }
    private void RememberUndo()
    {
        undo.Push(Copy(State)); if (undo.Count <= 64) return;
        var recent = undo.Take(64).Reverse().ToArray(); undo.Clear(); foreach (var state in recent) undo.Push(state);
    }
}
