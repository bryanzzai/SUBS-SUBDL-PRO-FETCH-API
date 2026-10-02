namespace SubdlProDownload.Services;

public interface ISubtitleProvider : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<SubtitleLookup> FindBestMatchAsync(
        string videoPath,
        string language,
        CancellationToken cancellationToken);

    Task DownloadAsync(
        SubtitleMatch match,
        string destinationPath,
        CancellationToken cancellationToken);
}

public sealed record SubtitleLookup(
    SubtitleMatch? Match,
    string Message);

public sealed record SubtitleMatch(
    string Provider,
    string SubtitleId,
    string? SourceFileName,
    double Score,
    string? ReleaseName);
