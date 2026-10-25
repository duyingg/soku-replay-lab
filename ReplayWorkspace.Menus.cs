namespace SokuReplayLab;

public sealed partial class ReplayWorkspace
{
    public bool SaveWorkspace(string name)
    {
        if (!Allowed || string.IsNullOrWhiteSpace(name) || name.Length > 60) return false;
        if (!workspaces.ContainsKey(name) && workspaces.Count >= 30) return false;
        Layout.Rename(name); workspaces[name] = ReplayDockLayout.Copy(Layout.State); Persist(); return true;
    }
    public bool LoadWorkspace(string name) => workspaces.TryGetValue(name, out var state) && Change(() => Layout.Restore(state, true));
}
