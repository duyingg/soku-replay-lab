using System.Buffers.Binary;
using System.IO;

namespace SokuReplayLab;

/// <summary>Matches observed input history to the REP input stream before a branch.</summary>
public static class ReplayInputAligner
{
    internal static ushort[] ReadInputWindow(string replayPath, IReadOnlyList<Frame> frames,
        int anchor, int side, int length)
    {
        if (side is not (0 or 1) || length is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(length));
        var bytes = File.ReadAllBytes(replayPath);
        var payload = PayloadOffset(bytes);
        var start = NextInputIndex(bytes, frames, anchor, payload);
        if (payload + (start + length) * 4 > bytes.Length)
            throw new InvalidDataException("输入窗口超出 REP。");
        return Enumerable.Range(0, length).Select(index =>
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(payload +
                (start + index) * 4 + side * 2))).ToArray();
    }
    public static int NextInputIndex(string replayPath, IReadOnlyList<Frame> frames, int anchor)
    {
        var bytes = File.ReadAllBytes(replayPath);
        return NextInputIndex(bytes, frames, anchor, PayloadOffset(bytes));
    }

    private static int PayloadOffset(byte[] bytes)
    {
        if (bytes.Length < 64 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0xd2)
            throw new InvalidDataException("REP 输入格式无效。");
        var first = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        if (first > 100) throw new InvalidDataException("REP 卡组数量异常。");
        var secondOffset = 20 + (int)first * 2;
        var second = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(secondOffset + 5));
        if (second > 100) throw new InvalidDataException("REP 卡组数量异常。");
        var payload = secondOffset + 9 + (int)second * 2 + 14;
        if (payload > bytes.Length) throw new InvalidDataException("REP 输入区不完整。");
        return payload;
    }

    private static int NextInputIndex(byte[] bytes, IReadOnlyList<Frame> frames,
        int anchor, int payload)
    {
        if (anchor < 32 || anchor >= frames.Count)
            throw new InvalidDataException("分支帧之前没有足够的输入历史。");
        var count = (bytes.Length - payload) / 4;
        const int history = 30;
        var candidates = new List<int>();
        var firstCandidate = Math.Max(0, anchor - 300 - history + 1);
        var lastCandidate = Math.Min(count - history - 1, anchor + 30 - history + 1);
        for (var index = firstCandidate; index <= lastCandidate; index++)
        {
            var matched = true;
            for (var j = 0; j < history; j++)
            {
                var frame = frames[anchor - history + 1 + j];
                var offset = payload + (index + j) * 4;
                var left = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
                var right = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2));
                if (left != frame.P1.Input || right != frame.P2.Input)
                {
                    matched = false;
                    break;
                }
            }
            if (matched) candidates.Add(index + history);
        }
        // Held directions can produce several identical 30-frame windows.
        // Extend backwards within this round until an earlier input edge makes
        // the match unique; never guess which repeated window is correct.
        for (var extra = history; candidates.Count > 1 && extra <= Math.Min(anchor, 600); extra++)
        {
            var observed = frames[anchor - extra];
            if (observed.MatchState != 2 || observed.Round != frames[anchor].Round) break;
            candidates.RemoveAll(next => {
                var index = next - extra - 1;
                if (index < 0) return true;
                var offset = payload + index * 4;
                return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) != observed.P1.Input ||
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2)) != observed.P2.Input;
            });
        }
        if (candidates.Count != 1)
            throw new InvalidDataException($"REP 输入与引擎轨迹无法唯一对齐（候选 {candidates.Count}）。");
        return candidates[0];
    }

    public static (string Path, int InputIndex) WriteBranchReplay(string replayPath,
        IReadOnlyList<Frame> baseline,
        int anchor, int side, IReadOnlyList<ushort> masks, string destination)
    {
        if (side is not (0 or 1) || masks.Count is < 1 or > 512 ||
            masks.Any(mask => (mask & ~0x3ff) != 0))
            throw new ArgumentException("分支需要 1P/2P 和 1～512 帧的有效输入。");
        var bytes = File.ReadAllBytes(replayPath);
        var payload = PayloadOffset(bytes);
        var start = NextInputIndex(bytes, baseline, anchor, payload);
        if (payload + (start + masks.Count) * 4 > bytes.Length)
            throw new InvalidDataException("分支输入超出 REP 末尾。");
        for (var i = 0; i < masks.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(payload + (start + i) * 4 + side * 2), masks[i]);
        destination = Path.GetFullPath(destination);
        File.WriteAllBytes(destination, bytes);
        return (destination, start);
    }
}
