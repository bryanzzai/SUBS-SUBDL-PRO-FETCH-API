namespace SubdlProDownload.Models;

public sealed record RawSubtitleRow(
    int QuerySeason,
    int RowNumber,
    string Kind,
    string HttpStatus,
    string SubtitleId,
    string ReleaseName,
    string SourceName,
    string SeasonValue,
    string EpisodeValue,
    string Details,
    string RawJson);
