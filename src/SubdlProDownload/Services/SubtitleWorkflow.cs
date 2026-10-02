using System.IO;
namespace SubdlProDownload.Services;

public sealed class SubtitleWorkflow(ISubtitleProvider provider)
{
    public async Task<SubtitleResult> DownloadForVideoAsync(
        string videoPath,
        string language,
        CancellationToken cancellationToken)
    {
        var lookup = await provider.FindBestMatchAsync(videoPath, language, cancellationToken);

        if (lookup.Match is null)
            return new SubtitleResult(false, null, lookup.Message);

        var destination = Path.ChangeExtension(videoPath, ".srt");

        await provider.DownloadAsync(lookup.Match, destination, cancellationToken);

        return new SubtitleResult(
            true,
            destination,
            $"Downloaded (SubDL match {lookup.Match.Score:0.00}).");
    }
}

public sealed record SubtitleResult(
    bool Success,
    string? SubtitlePath,
    string Message);
