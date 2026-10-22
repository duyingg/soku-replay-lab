using System.Globalization;
using System.IO;
using System.Text;

namespace SokuReplayLab;

public static class EventCsvExporter
{
    public static string FormatTime(int ordinal)
    {
        var elapsed = TimeSpan.FromSeconds(ordinal * ReplayTiming.SecondsPerFrame);
        return $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}";
    }

    public static int Export(string path, IEnumerable<ReplayEvent> events)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("轨迹帧,时间,回合,角色,事件,细节,依据");
        var count = 0;
        foreach (var item in events)
        {
            writer.WriteLine(string.Join(",", new[] {
                item.Ordinal.ToString(CultureInfo.InvariantCulture), FormatTime(item.Ordinal),
                (item.Round + 1).ToString(CultureInfo.InvariantCulture),
                item.Side == -1 ? "全局" : item.Side == 0 ? "1P" : "2P",
                item.Kind, item.Detail, item.Reason
            }.Select(Cell)));
            count++;
        }
        return count;
    }

    private static string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
