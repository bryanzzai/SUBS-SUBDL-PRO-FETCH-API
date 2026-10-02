using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SubdlProDownload.Models;

public sealed class SeasonPackItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status;

    public SeasonPackItem(string subtitleId, string seasonLabel, string packageName, string? sourceFileName, bool isDownloadable = true, string? status = null)
    {
        SubtitleId = subtitleId;
        SeasonLabel = seasonLabel;
        PackageName = packageName;
        SourceFileName = sourceFileName ?? "—";
        IsDownloadable = isDownloadable;
        _status = status ?? "Available";
    }

    public string SubtitleId { get; }
    public string SeasonLabel { get; }
    public string PackageName { get; }
    public string SourceFileName { get; }
    public bool IsDownloadable { get; }

    public static SeasonPackItem NotFound(int season, string mask) => new(
        string.Empty,
        $"Season {season}",
        $"No match for {mask}",
        "—",
        isDownloadable: false,
        status: "Not found");

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
