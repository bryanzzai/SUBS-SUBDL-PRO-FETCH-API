using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SubdlProDownload.Models;

public sealed class RawSubtitleRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _downloadStatus;

    public RawSubtitleRow(
        int querySeason,
        int rowNumber,
        string kind,
        string httpStatus,
        string packageId,
        string subtitlePage,
        string downloadUrl,
        string downloadUrlDisplay,
        string releaseName,
        string sourceName,
        string seasonValue,
        string episodeValue,
        bool isHearingImpaired,
        string details,
        string rawJson,
        string? downloadStatus = null)
    {
        QuerySeason = querySeason;
        RowNumber = rowNumber;
        Kind = kind;
        HttpStatus = httpStatus;
        PackageId = packageId;
        SubtitlePage = subtitlePage;
        DownloadUrl = downloadUrl;
        DownloadUrlDisplay = downloadUrlDisplay;
        ReleaseName = releaseName;
        SourceName = sourceName;
        SeasonValue = seasonValue;
        EpisodeValue = episodeValue;
        IsHearingImpaired = isHearingImpaired;
        Details = details;
        RawJson = rawJson;
        _downloadStatus = downloadStatus ?? (IsRawRow ? "Ready" : "—");
    }

    public int QuerySeason { get; }
    public int RowNumber { get; }
    public string Kind { get; }
    public string HttpStatus { get; }
    public string PackageId { get; }
    public string SubtitlePage { get; }
    public string DownloadUrl { get; }
    public string DownloadUrlDisplay { get; }
    public string ReleaseName { get; }
    public string SourceName { get; }
    public string SeasonValue { get; }
    public string EpisodeValue { get; }
    public bool IsHearingImpaired { get; }
    public string HiDisplay => IsHearingImpaired ? "●" : string.Empty;
    public string Details { get; }
    public string RawJson { get; }

    public bool IsRawRow => Kind == "RAW";
    public bool HasDownloadUrl => IsRawRow && !string.IsNullOrWhiteSpace(DownloadUrl) && DownloadUrl != "—";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, IsRawRow && value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        set => SetField(ref _downloadStatus, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
