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
        string subtitleId,
        string releaseName,
        string sourceName,
        string seasonValue,
        string episodeValue,
        string details,
        string rawJson,
        string? downloadStatus = null)
    {
        QuerySeason = querySeason;
        RowNumber = rowNumber;
        Kind = kind;
        HttpStatus = httpStatus;
        SubtitleId = subtitleId;
        ReleaseName = releaseName;
        SourceName = sourceName;
        SeasonValue = seasonValue;
        EpisodeValue = episodeValue;
        Details = details;
        RawJson = rawJson;
        _downloadStatus = downloadStatus ?? (IsDownloadable ? "Ready" : "—");
    }

    public int QuerySeason { get; }
    public int RowNumber { get; }
    public string Kind { get; }
    public string HttpStatus { get; }
    public string SubtitleId { get; }
    public string ReleaseName { get; }
    public string SourceName { get; }
    public string SeasonValue { get; }
    public string EpisodeValue { get; }
    public string Details { get; }
    public string RawJson { get; }

    public bool IsDownloadable => Kind == "RAW" && !string.IsNullOrWhiteSpace(SubtitleId) && SubtitleId != "—";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsDownloadable) value = false;
            SetField(ref _isSelected, value);
        }
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
