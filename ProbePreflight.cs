using System.Diagnostics;

namespace SokuReplayLab;

public sealed record ProbeSlotCheck(string Name, string Address, string? Original,
    string? OwnerModule, bool PointerReadable);
public sealed record ProbePreflightResult(int Pid, long Window, string Executable,
    bool ExecutableVerified, bool MainWindowVerified, bool PeHeaderReadable,
    bool FunctionsReadable, bool ReplayManagerReadable, bool FramePacingReadable,
    string? BattlePointer, IReadOnlyList<ProbeSlotCheck> Slots,
    IReadOnlyList<string> LoadedProbeModules, bool Passed);

/// <summary>Checks fixed 1.10a probe addresses without modifying the game.</summary>
public static class ProbePreflight
{
    public static ProbePreflightResult Inspect(GameInstance game)
    {
        GameDiscovery.VerifyGameFile(game.Exe);
        var window = GameDiscovery.Find().Any(x => x.Pid == game.Pid && x.Window == game.Window);
        using var reader = new LiveGameReader(game);
        var mz = reader.TryReadByte(0x00400000, out var m) && m == (byte)'M' &&
            reader.TryReadByte(0x00400001, out var z) && z == (byte)'Z';
        var functions = reader.TryReadByte(0x0042EAC0, out var readReplay) && readReplay != 0 &&
            reader.TryReadByte(0x0043E9A0, out var setBattleMode) && setBattleMode != 0;
        var manager = reader.TryReadWord(0x00898718, out _);
        var pacing = reader.TryReadWord(0x008A0FF8, out _);
        var battle = reader.TryReadWord(0x008985E4, out var battlePointer)
            ? $"0x{battlePointer:X8}" : null;
        using var process = Process.GetProcessById(game.Pid);
        ProcessModule[] allModules;
        try { allModules = process.Modules.Cast<ProcessModule>().ToArray(); }
        catch (System.ComponentModel.Win32Exception) { allModules = []; }
        var slots = new[] {
            (Name: "logo", Address: 0x00857744u),
            (Name: "title", Address: 0x00857FB0u),
            (Name: "battle", Address: 0x008574A4u)
        }.Select(item => {
            var readable = reader.TryReadWord(item.Address, out var original);
            var target = readable && original is >= 0x10000 and < 0x7fff0000 &&
                reader.TryReadByte(original, out _);
            var owner = allModules.FirstOrDefault(x =>
                original >= x.BaseAddress.ToInt64() &&
                original < x.BaseAddress.ToInt64() + x.ModuleMemorySize)?.ModuleName;
            return new ProbeSlotCheck(item.Name, $"0x{item.Address:X8}",
                readable ? $"0x{original:X8}" : null, owner, target);
        }).ToArray();
        var modules = allModules
            .Select(x => x.ModuleName)
            .Where(x => x.Equals("NativeProbe.dll", StringComparison.OrdinalIgnoreCase) ||
                x.StartsWith("EngineProbe.", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var passed = window && mz && functions && manager && pacing &&
            slots.All(x => x.PointerReadable) && modules.Length == 0;
        return new ProbePreflightResult(game.Pid, game.Window.ToInt64(), game.Exe,
            true, window, mz, functions, manager, pacing, battle, slots, modules, passed);
    }
}
