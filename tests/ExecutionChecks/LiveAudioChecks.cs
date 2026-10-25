using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using SokuReplayLab;

internal static class LiveAudioChecks
{
    // Read the real DirectSound play cursor, not the app's reported pause flag.
    public static async Task RunAsync(string exe, string replay, string output)
    {
        Directory.CreateDirectory(output);
        using var process = GameDiscovery.Launch(exe);
        ProbeClient? client = null;
        var observations = new List<object>();
        try
        {
            GameInstance? game = null;
            for (var i = 0; i < 100 && game is null; i++)
            {
                game = GameDiscovery.FindProcesses().FirstOrDefault(g => g.Pid == process.Id && g.Window != 0);
                if (game is null) await Task.Delay(100);
            }
            if (game is null) throw new IOException("Test game did not start.");
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            client = await ProbeClient.AttachAsync(game, project);
            using var memory = new GameAudioMemory(process.Id);
            await client.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: Path.Combine(output, "audio.csv"));
            await WaitForBattle();
            await Observe("playing", false);
            await client.SendAsync(ProbeCommand.Pause);
            await Observe("paused", true);
            await client.SendAsync(ProbeCommand.Speed, 200);
            await Observe("speed while paused", true);
            await client.SendAsync(ProbeCommand.Play);
            await Observe("resumed", false);
            await client.SendAsync(ProbeCommand.Seek, 300);
            await Observe("seek paused", true);
            await client.SendAsync(ProbeCommand.SaveSnapshot, 300);
            await client.SendAsync(ProbeCommand.Play);
            await Task.Delay(200);
            await client.SendAsync(ProbeCommand.Pause);
            await client.SendAsync(ProbeCommand.RestoreSnapshot, 300);
            await Observe("snapshot paused", true);
            await client.SendAsync(ProbeCommand.FreeControl);
            await Observe("takeover", false);
            await client.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: Path.Combine(output, "audio.csv"));
            await WaitForBattle();
            try { await client.SendAsync(ProbeCommand.Seek, 100000); }
            catch (IOException) when (client.Read().Status == -5) { }
            await Observe("replay end", true);
            await client.SendAsync(ProbeCommand.Release);
            await Task.Delay(1000);
            await Observe("released", false);
            Console.WriteLine("PASS: actual music cursor pauses and resumes across playback, seek, snapshot, end and takeover.");

            async Task Observe(string phase, bool paused)
            {
                await Task.Delay(150);
                var before = memory.Sample();
                await Task.Delay(400);
                var after = memory.Sample();
                observations.Add(new { phase, before, after });
                if (before.Track != after.Track || before.State != (paused ? 3u : 0u) ||
                    after.State != before.State || (before.Cursor == after.Cursor) != paused)
                    throw new IOException("Music cursor/state mismatch: " + phase);
            }
            async Task WaitForBattle()
            {
                var clock = Stopwatch.StartNew();
                while (client.Read().Ordinal < 120)
                {
                    if (clock.Elapsed.TotalSeconds > 30) throw new IOException("Battle did not start.");
                    await Task.Delay(100);
                }
            }
        }
        finally
        {
            client?.Dispose();
            await GameDiscovery.CloseVerifiedGameAsync(process.Id, exe);
            File.WriteAllText(Path.Combine(output, "audio-check.json"), JsonSerializer.Serialize(observations,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private sealed class GameAudioMemory : IDisposable
    {
        private readonly nint process;
        private readonly nint scratch;
        public GameAudioMemory(int pid)
        {
            process = OpenProcess(0x43A, false, pid);
            if (process == 0) throw new IOException("Cannot open test game.");
            scratch = VirtualAllocEx(process, 0, 4096, 0x3000, 0x40);
            if (scratch == 0) throw new IOException("Cannot allocate audio check stub.");
        }
        public record Audio(uint Track, uint State, uint Cursor);
        public Audio Sample()
        {
            var track = Read(0x899D5C);
            var code = new List<byte>();
            void Imm(byte op, uint value) { code.Add(op); code.AddRange(BitConverter.GetBytes(value)); }
            Imm(0x68, track); // push track
            Imm(0xB9, 0x89FE88); // music pool
            Imm(0xB8, 0x4045F0); // lookup, locks the game's handle pool
            code.AddRange([0xFF, 0xD0, 0xC2, 4, 0]);
            var bgm = Call(code.ToArray());
            if (bgm == 0) throw new IOException("No music object.");
            var buffer = Read(bgm + 0x28);
            if (buffer == 0) throw new IOException("No DirectSound music buffer.");
            code.Clear();
            Imm(0x68, 0); // write cursor not needed
            Imm(0x68, checked((uint)scratch + 1024)); // play cursor output
            Imm(0x68, buffer);
            Imm(0xB8, Read(Read(buffer) + 16)); // IDirectSoundBuffer::GetCurrentPosition
            code.AddRange([0xFF, 0xD0, 0xC2, 4, 0]);
            if (Call(code.ToArray()) != 0) throw new IOException("DirectSound cursor read failed.");
            return new(track, Read(bgm + 0x1C), Read(checked((uint)scratch + 1024)));
        }
        private uint Call(byte[] code)
        {
            if (!WriteProcessMemory(process, scratch, code, (nuint)code.Length, out _)) throw new IOException("Stub write failed.");
            FlushInstructionCache(process, scratch, (nuint)code.Length);
            var thread = CreateRemoteThread(process, 0, 0, scratch, 0, 0, out _);
            if (thread == 0) throw new IOException("Stub start failed.");
            try
            {
                if (WaitForSingleObject(thread, 3000) != 0 || !GetExitCodeThread(thread, out var result))
                    throw new IOException("Audio read timed out.");
                return result;
            }
            finally { CloseHandle(thread); }
        }
        private uint Read(uint address)
        {
            var value = new byte[4];
            if (!ReadProcessMemory(process, (nint)address, value, 4, out _)) throw new IOException("Audio memory read failed.");
            return BitConverter.ToUInt32(value);
        }
        public void Dispose() { VirtualFreeEx(process, scratch, 0, 0x8000); CloseHandle(process); }
        [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocation, uint protect);
        [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint type);
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);
        [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);
        [DllImport("kernel32.dll")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
        [DllImport("kernel32.dll")] private static extern nint CreateRemoteThread(nint process, nint attributes, nuint stack, nint start, nint argument, uint flags, out uint id);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeThread(nint thread, out uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    }
}
