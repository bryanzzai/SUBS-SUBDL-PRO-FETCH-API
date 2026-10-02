using System.IO;
using SubdlProDownload.Models;
namespace SubdlProDownload.Services;

public sealed class SubtitleWorkflow(ISubtitleProvider provider)
{
    public async Task<SubtitleResult> DownloadForVideoAsync(
        VideoItem video,
        TitleCandidate? selectedTitle,
        string language,
        CancellationToken cancellationToken)
    {
        var lookup = await provider.FindBestMatchAsync(video, selectedTitle, language, cancellationToken);

        if (lookup.Match is null)
            return new SubtitleResult(false, null, lookup.Message);

        var destination = Path.ChangeExtension(video.FullPath, ".srt");

        await provider.DownloadAsync(lookup.Match, destination, cancellationToken);

        return new SubtitleResult(
            true,
            destination,
            $"Downloaded ({lookup.Match.MatchKind}; score {lookup.Match.Score:0.00}).");
    }
}

public sealed record SubtitleResult(
    bool Success,
    string? SubtitlePath,
    string Message);
