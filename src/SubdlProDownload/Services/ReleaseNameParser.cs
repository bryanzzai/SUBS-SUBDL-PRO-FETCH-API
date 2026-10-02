using System.IO;
using System.Text.RegularExpressions;

namespace SubdlProDownload.Services;

public static partial class ReleaseNameParser
{
    public static string BuildSearchQuery(string videoPath)
    {
        var name = Path.GetFileNameWithoutExtension(videoPath)
            .Replace('.', ' ')
            .Replace('_', ' ');

        name = BracketedTags().Replace(name, " ");
        name = ReleaseNoise().Replace(name, " ");
        name = Whitespace().Replace(name, " ").Trim();

        return name;
    }

    [GeneratedRegex(@"[[{].*?[]}]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedTags();

    [GeneratedRegex(
        @"(2160p|1080p|720p|480p|webrip|web-dl|bluray|brrip|bdrip|dvdrip|hdrip|hdtv|xvid|x264|x265|h.?264|h.?265|hevc|aac|ac3|ddp?5.?1|atmos|proper|repack|unrated|extended|remastered|netflix|nf|amzn).*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseNoise();

    [GeneratedRegex(@"s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
