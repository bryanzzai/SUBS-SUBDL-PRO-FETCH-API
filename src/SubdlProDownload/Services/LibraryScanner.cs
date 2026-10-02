using System.IO;
using SubdlProDownload.Models;

namespace SubdlProDownload.Services;

public sealed class LibraryScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts"
    };

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Subs", "Subtitles", "Sample", "Samples"
    };

    public IReadOnlyList<VideoItem> Scan(string rootPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Library path is required.", nameof(rootPath));

        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException(rootPath);

        var items = new List<VideoItem>();
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var name = Path.GetFileName(directory);
                    if (!IgnoredDirectoryNames.Contains(name))
                        pending.Push(directory);
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!VideoExtensions.Contains(Path.GetExtension(file)))
                        continue;

                    items.Add(new VideoItem
                    {
                        FullPath = file,
                        FileName = Path.GetFileName(file),
                        Folder = current,
                        Identity = MediaIdentityParser.Parse(file),
                        SubtitlePath = FindExistingSubtitle(file),
                        Status = "Ready"
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip folders that the current Windows user cannot read.
            }
            catch (IOException)
            {
                // Skip temporarily unavailable folders and continue the scan.
            }
        }

        return items
            .OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FindExistingSubtitle(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath)!;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);

        var exact = Path.Combine(directory, baseName + ".srt");
        if (File.Exists(exact))
            return exact;

        var english = Path.Combine(directory, baseName + ".en.srt");
        if (File.Exists(english))
            return english;

        return null;
    }
}
