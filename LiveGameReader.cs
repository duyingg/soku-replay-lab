using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SokuReplayLab;

public sealed record LiveFighter(int Character, float X, float Y, int Action,
    int Hp, int Spirit, int ComboCount, int ComboDamage);

public sealed record LiveGameStatus(uint EngineFrame, int MatchState, int Round,
    int Weather, LiveFighter Player1, LiveFighter Player2);

/// <summary>Reads a verified 1.10a process without writing game memory.</summary>
public sealed class LiveGameReader : IDisposable
{
    private const uint QueryLimited = 0x1000;
    private const uint VmRead = 0x0010;
    private const uint StillActive = 259;
    private const uint BattlePointer = 0x008985E4;
    private const uint WeatherAddress = 0x008971C0;
    private readonly nint process;
    private bool disposed;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address,
        [Out] byte[] buffer, nuint count, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    public int Pid { get; }

    public LiveGameReader(GameInstance instance)
    {
        GameDiscovery.VerifyGameFile(instance.Exe);
        Pid = instance.Pid;
        process = OpenProcess(QueryLimited | VmRead, false, checked((uint)instance.Pid));
        if (process == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法以只读方式打开游戏进程。");
    }

    public bool IsAlive => !disposed && GetExitCodeProcess(process, out var code) && code == StillActive;

    public bool TryReadBattlePointer(out uint address)
    {
        address = 0;
        if (!IsAlive) return false;
        try { address = ReadU32(BattlePointer); return true; }
        catch (Win32Exception) { return false; }
    }

    public bool TryReadWord(uint address, out uint value)
    {
        value = 0;
        if (!IsAlive) return false;
        try { value = ReadU32(address); return true; }
        catch (Win32Exception) { return false; }
    }

    public bool TryReadByte(uint address, out byte value)
    {
        value = 0;
        if (!IsAlive) return false;
        try { value = ReadU8(address); return true; }
        catch (Win32Exception) { return false; }
    }

    public bool TryRead(out LiveGameStatus? status)
    {
        status = null;
        if (!IsAlive) return false;
        try
        {
            var battle = Pointer(ReadU32(BattlePointer));
            var left = Pointer(ReadU32(battle + 0x0C));
            var right = Pointer(ReadU32(battle + 0x10));
            var before = ReadU32(battle + 4);
            var matchState = ReadI32(battle + 0x88);
            var round = unchecked((sbyte)ReadU8(battle + 0x904));
            var weather = checked((int)ReadU32(WeatherAddress));
            var p1 = ReadFighter(left);
            var p2 = ReadFighter(right);
            var after = ReadU32(battle + 4);
            if (before != after) return false;
            if (p1.Hp is < 0 or > 10000 || p2.Hp is < 0 or > 10000 ||
                p1.Spirit is < 0 or > 5000 || p2.Spirit is < 0 or > 5000 ||
                weather > 32 || round is < -1 or > 9) return false;
            status = new LiveGameStatus(after, matchState, round, weather, p1, p2);
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private LiveFighter ReadFighter(uint player) => new(
        ReadU8(player + 0x34C), ReadF32(player + 0xEC), ReadF32(player + 0xF0),
        ReadU16(player + 0x13C), ReadI16(player + 0x184), ReadU16(player + 0x49E),
        ReadU16(player + 0x4B4), ReadU16(player + 0x4B6));

    private byte ReadU8(uint address) => Read(address, 1)[0];
    private ushort ReadU16(uint address) => BitConverter.ToUInt16(Read(address, 2));
    private short ReadI16(uint address) => BitConverter.ToInt16(Read(address, 2));
    private uint ReadU32(uint address) => BitConverter.ToUInt32(Read(address, 4));
    private int ReadI32(uint address) => BitConverter.ToInt32(Read(address, 4));
    private float ReadF32(uint address) => BitConverter.ToSingle(Read(address, 4));

    private byte[] Read(uint address, int size)
    {
        if (disposed) throw new ObjectDisposedException(nameof(LiveGameReader));
        var bytes = new byte[size];
        if (!ReadProcessMemory(process, (nint)address, bytes, (nuint)size, out var read) ||
            read != (nuint)size)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "游戏状态读取失败。");
        return bytes;
    }

    private static uint Pointer(uint address)
    {
        if (address is < 0x10000 or >= 0x7FFF0000)
            throw new InvalidOperationException("游戏指针无效。");
        return address;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CloseHandle(process);
    }
}
