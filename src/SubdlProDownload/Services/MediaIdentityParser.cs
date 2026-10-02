using System.IO;
using System.Text.RegularExpressions;
using SubdlProDownload.Models;

namespace SubdlProDownload.Services;

/// <summary>Derives a human-oriented title and episode from both the filename and its folders.</summary>
public static class MediaIdentityParser
{
    private static readonly Regex StandardEpisode = new(@"(?<![a-z0-9])s(?<season>\d{1,2})[ ._-]*e(?<episode>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex XEpisode = new(@"(?<!\d)(?<season>\d{1,2})\s*x\s*(?<episode>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SeasonFolder = new(@"(?:season|series)\s*0?(?<season>\d{1,2})|^s0?(?<shortSeason>\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CompactEpisode = new(@"(?<!\d)(?<season>\d{2})(?<episode>\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex DashEpisode = new(@"(?<!\d)(?<season>\d{1,2})\s*-\s*(?<episode>\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex EpisodeOnly = new(@"(?:episode|ep)\s*0?(?<episode>\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReleaseNoise = new(@"\b(2160p|1080p|720p|576p|480p|web[ .-]?(dl|rip)|blu[ .-]?ray|brrip|hdtv|dvdrip|x26[45]|h\.?(26[45])|hevc|av1|aac|ac3|ddp?\d?\.\d|atmos|proper|repack|extended|remastered|nf|amzn|dsnp)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Separators = new(@"[._]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "tv", "tv shows", "tvshows", "series", "shows", "video", "videos", "movies", "media", "downloads", "download"
    };

    public static MediaIdentity Parse(string videoPath)
    {
        var fileStem = Path.GetFileNameWithoutExtension(videoPath);
        var folderSeason = FindSeasonInFolders(videoPath);
        var (season, episode, evidence) = FindEpisode(fileStem, folderSeason);
        var title = FindTitle(videoPath, fileStem, season, episode);
        var key = NormalizeKey(title);
        return new MediaIdentity(key, title, season, episode, evidence);
    }

    private static (int? Season, int? Episode, string Evidence) FindEpisode(string stem, int? folderSeason)
    {
        foreach (var (match, evidence) in new[] { (StandardEpisode.Match(stem), "SxxExx"), (XEpisode.Match(stem), "x notation") })
        {
            if (match.Success)
                return (Number(match, "season"), Number(match, "episode"), evidence);
        }

        if (folderSeason is not null)
        {
            var compact = CompactEpisode.Match(stem);
            if (compact.Success && Number(compact, "season") == folderSeason)
                return (folderSeason, Number(compact, "episode"), "compact season/episode");

            var dashed = DashEpisode.Match(stem);
            if (dashed.Success && Number(dashed, "season") == folderSeason)
                return (folderSeason, Number(dashed, "episode"), "dash season/episode");

            var episodeOnly = EpisodeOnly.Match(stem);
            if (episodeOnly.Success)
                return (folderSeason, Number(episodeOnly, "episode"), "season folder + episode");
        }

        return (null, null, "title only");
    }

    private static int? FindSeasonInFolders(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var match = SeasonFolder.Match(Path.GetFileName(directory));
            if (match.Success)
            {
                var value = match.Groups["season"].Success ? Number(match, "season") : Number(match, "shortSeason");
                if (value is > 0) return value;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    private static string FindTitle(string videoPath, string stem, int? season, int? episode)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var childWasSeasonFolder = false;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var folder = Path.GetFileName(directory).Trim();
            var isSeasonFolder = SeasonFolder.IsMatch(folder);
            if (!string.IsNullOrWhiteSpace(folder)
                && !isSeasonFolder
                && !GenericFolders.Contains(folder)
                && (childWasSeasonFolder || StemStartsWithFolder(stem, folder)))
                return CleanTitle(folder);
            childWasSeasonFolder = isSeasonFolder;
            directory = Path.GetDirectoryName(directory);
        }

        var titlePart = stem;
        var markers = new[] { StandardEpisode.Match(stem), XEpisode.Match(stem) }.Where(match => match.Success).ToArray();
        if (markers.Length > 0)
            titlePart = stem[..markers.Min(match => match.Index)];
        else if (season is not null && episode is not null)
        {
            var compact = CompactEpisode.Match(stem);
            if (compact.Success) titlePart = stem[..compact.Index];
        }
        return CleanTitle(titlePart);
    }

    private static string CleanTitle(string value)
    {
        value = Separators.Replace(value, " ");
        value = ReleaseNoise.Replace(value, " ");
        value = Whitespace.Replace(value, " ").Trim(' ', '-', '_', '.');
        return string.IsNullOrWhiteSpace(value) ? "Unknown title" : value;
    }

    private static string NormalizeKey(string title) => Whitespace.Replace(Separators.Replace(title, " "), " ").Trim().ToUpperInvariant();
    private static bool StemStartsWithFolder(string stem, string folder)
    {
        var stemKey = NormalizeKey(stem);
        var folderKey = NormalizeKey(folder);
        return stemKey.StartsWith(folderKey + " ", StringComparison.Ordinal) || stemKey == folderKey;
    }
    private static int Number(Match match, string group) => int.Parse(match.Groups[group].Value);
}
