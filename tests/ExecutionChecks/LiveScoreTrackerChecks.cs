using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using SokuReplayLab;

internal static class LiveScoreTrackerChecks
{
    public static async Task RunAsync(string exe, string replay, string output)
    {
        Directory.CreateDirectory(output);
        using var process = GameDiscovery.Launch(exe);
        ProbeClient? client = null;
        GameWindowLease? lease = null;
        Window? host = null;
        var observations = new List<object>();
        Exception? error = null;
        try
        {
            GameInstance? game = null;
            for (var i = 0; i < 100 && game is null; i++)
            {
                game = GameDiscovery.FindProcesses().FirstOrDefault(g => g.Pid == process.Id && g.Window != 0);
                if (game is null) await Task.Delay(100);
            }
            if (game is null) throw new IOException("Test game did not start.");
            var mod = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "ScoreTracker.dll");
            var modBase = checked((uint)mod.BaseAddress);
            var modFile = mod.FileName;
            var modHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modFile)));
            using var memory = new RemoteMemory(process.Id);
            // Test-only offsets from the exact MOD in the crash dump. Production
            // code resolves the import by name and contains no MOD RVA constants.
            var pe = memory.Read(modBase + 0x3C);
            if (mod.ModuleMemorySize != 0xED000 || memory.Read(modBase + pe + 8) != 0x6301AB78)
                throw new IOException("This diagnostic requires the ScoreTracker build from the dump.");
            var originalLookup = memory.Read(modBase + 0x97220);
            var beforeContext = memory.Read(modBase + 0xB9FC0);
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            client = await ProbeClient.AttachAsync(game, project);
            var adaptedLookup = memory.Read(modBase + 0x97220);
            if (adaptedLookup == originalLookup) throw new IOException("ScoreTracker window lookup was not adapted.");
            host = new Window { Title = "ScoreTracker compatibility check", Width = 840, Height = 680 };
            host.Show();
            lease = new GameWindowLease(game, new WindowInteropHelper(host).Handle);
            var attachClock = Stopwatch.StartNew();
            while (true)
            {
                try { lease.Attach(800, 600); break; }
                catch (InvalidOperationException) when (!process.HasExited && attachClock.Elapsed.TotalSeconds < 20)
                { await Task.Delay(500); }
            }
            await Task.Delay(200);
            var plain = memory.FindWindow(originalLookup, "th123_110a");
            var adapted = memory.FindWindow(adaptedLookup, "th123_110a");
            var unknown = memory.FindWindow(adaptedLookup, "ReplayLabNonexistentClass");
            var wrongTitle = memory.FindWindow(adaptedLookup, "th123_110a", "ReplayLabNonexistentTitle");
            if (plain != 0 || adapted != checked((uint)game.Window) || unknown != 0 || wrongTitle != 0)
                throw new IOException("Embedded-window lookup or unrelated-search behavior differs.");
            observations.Add(new { phase = "lookup", beforeContext, originalLookup, adaptedLookup,
                plain, adapted, expected = (long)game.Window, unknown, wrongTitle, modHash });
            // The local INI hides the offline overlay. Reproduce the dump's
            // enabled loading-screen overlay in this owned test process only.
            // No MOD configuration or game file is written.
            memory.SetByte(modBase + 0xBA694, 1);
            memory.SetByte(modBase + 0xBA6BC, 1);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                await client.SendAsync(ProbeCommand.Load, replayPath: replay, tracePath: Path.Combine(output, "compatibility.csv"));
                var clock = Stopwatch.StartNew();
                while (client.Read().Ordinal < 124 || memory.Read(modBase + 0xB9FC0) == 0)
                {
                    if (process.HasExited || clock.Elapsed.TotalSeconds > 40)
                        throw new IOException("Embedded replay/ScoreTracker context did not initialize.");
                    await Task.Delay(100);
                }
                await client.SendAsync(ProbeCommand.Pause);
                await client.SendAsync(ProbeCommand.Seek, 300);
                await client.SendAsync(ProbeCommand.SaveSnapshot, 300);
                await client.SendAsync(ProbeCommand.Seek, 450);
                await client.SendAsync(ProbeCommand.RestoreSnapshot, 300);
                await client.SendAsync(ProbeCommand.Play);
                await Task.Delay(500);
                await client.SendAsync(ProbeCommand.Pause);
                observations.Add(new { phase = "embedded replay", cycle,
                    context = memory.Read(modBase + 0xB9FC0), state = client.Read() });
                await client.SendAsync(ProbeCommand.Release);
                if (!lease.Release()) throw new IOException("Game window did not restore.");
                await Task.Delay(300);
                if (memory.FindWindow(adaptedLookup, "th123_110a") != checked((uint)game.Window))
                    throw new IOException("Detached lookup failed.");
                lease.Attach(800, 600);
            }
            if (process.HasExited || memory.Read(modBase + 0xB9FC0) == 0 ||
                modHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modFile))))
                throw new IOException("MOD context was lost or its file was changed.");
            observations.Add(new { phase = "complete", contextAlive = true, modFileUnchanged = true });
            Console.WriteLine("PASS: ScoreTracker lookup, real context initialization, two embedded replays, seeks, snapshots and window restoration.");
        }
        catch (Exception ex) { error = ex; }
        finally
        {
            lease?.Release();
            client?.Dispose();
            await GameDiscovery.CloseVerifiedGameAsync(process.Id, exe);
            host?.Close();
            File.WriteAllText(Path.Combine(output, "scoretracker-check.json"), JsonSerializer.Serialize(new {
                observations, error = error?.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (error is not null) throw error;
    }

    private sealed class RemoteMemory : IDisposable
    {
        private readonly nint process;
        private readonly nint scratch;
        public RemoteMemory(int pid)
        {
            process = OpenProcess(0x43A, false, pid);
            if (process == 0) throw new IOException("Cannot open test game.");
            scratch = VirtualAllocEx(process, 0, 4096, 0x3000, 0x40);
            if (scratch == 0) throw new IOException("Cannot allocate test stub.");
        }
        public uint Read(uint address)
        {
            var bytes = new byte[4];
            if (!ReadProcessMemory(process, (nint)address, bytes, 4, out _)) throw new IOException("Read failed.");
            return BitConverter.ToUInt32(bytes);
        }
        public uint FindWindow(uint function, string className, string? title = null)
        {
            var classPointer = checked((uint)scratch + 1024);
            Write((nint)classPointer, Encoding.Unicode.GetBytes(className + '\0'));
            var titlePointer = title is null ? 0 : checked((uint)scratch + 1536);
            if (title is not null) Write((nint)titlePointer, Encoding.Unicode.GetBytes(title + '\0'));
            var code = new List<byte>();
            void Imm(byte op, uint value) { code.Add(op); code.AddRange(BitConverter.GetBytes(value)); }
            Imm(0x68, titlePointer); Imm(0x68, classPointer); Imm(0xB8, function);
            code.AddRange([0xFF, 0xD0, 0xC2, 4, 0]);
            Write(scratch, code.ToArray());
            FlushInstructionCache(process, scratch, (nuint)code.Count);
            var thread = CreateRemoteThread(process, 0, 0, scratch, 0, 0, out _);
            if (thread == 0) throw new IOException("Test call failed.");
            try
            {
                if (WaitForSingleObject(thread, 3000) != 0 || !GetExitCodeThread(thread, out var result))
                    throw new IOException("Test call timed out.");
                return result;
            }
            finally { CloseHandle(thread); }
        }
        private void Write(nint address, byte[] bytes)
        {
            if (!WriteProcessMemory(process, address, bytes, (nuint)bytes.Length, out _)) throw new IOException("Test write failed.");
        }
        public void SetByte(uint address, byte value) => Write((nint)address, [value]);
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
