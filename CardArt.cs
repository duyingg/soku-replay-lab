using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;

namespace SokuReplayLab;

/// <summary>Loads the original card pictures from the portable resource archive.</summary>
public static class CardArt
{
    private static readonly string[] CharacterDirs = [
        "reimu", "marisa", "sakuya", "alice", "patchouli", "youmu",
        "remilia", "yuyuko", "yukari", "suika", "udonge", "aya",
        "komachi", "iku", "tenshi", "sanae", "chirno", "meirin",
        "utsuho", "suwako"
    ];
    private static readonly Dictionary<(int Character, int Card), BitmapSource?> Cache = [];
    private static readonly object Gate = new();
    private static string? archivePath;
    private static ZipArchive? archive;
    private static bool archiveLocated;

    public static BitmapSource? Get(int character, int card)
    {
        if (card < 0 || character < 0 || character >= CharacterDirs.Length) return null;
        lock (Gate)
        {
            var key = (character, card);
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (!archiveLocated)
            {
                archivePath = FindArchive();
                archiveLocated = true;
            }
            if (archivePath is null) return Cache[key] = null;
            try
            {
                archive ??= new ZipArchive(
                    new MemoryStream(File.ReadAllBytes(archivePath), writable: false),
                    ZipArchiveMode.Read);
                var directory = card < 100 ? "common" : CharacterDirs[character];
                var entry = archive.GetEntry($"{directory}/{card:000}.png");
                if (entry is null) return Cache[key] = null;
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = copy;
                image.EndInit();
                image.Freeze();
                return Cache[key] = image;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return Cache[key] = null;
            }
        }
    }

    private static string? FindArchive()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "CardArt.zip");
        if (File.Exists(output)) return output;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "CardArt.zip");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }
}
