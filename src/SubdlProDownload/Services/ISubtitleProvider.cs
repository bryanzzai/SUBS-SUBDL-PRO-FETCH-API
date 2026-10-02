using SubdlProDownload.Models;

namespace SubdlProDownload.Services;

public interface ISubtitleProvider : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TitleCandidate>> SearchTitlesAsync(
        string query,
        CancellationToken cancellationToken);

    Task<SubtitleLookup> FindBestMatchAsync(
        VideoItem video,
        TitleCandidate? selectedTitle,
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
    string? ReleaseName,
    string MatchKind,
    int? ExpectedSeason,
    int? ExpectedEpisode);
