using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SubdlProDownload.Models;

public sealed class TitleMapping : INotifyPropertyChanged
{
    private TitleCandidate? _selectedCandidate;
    private string _status = "Not searched";

    public required string GroupKey { get; init; }
    public required string SuggestedTitle { get; init; }
    public required string Episodes { get; init; }
    public ObservableCollection<TitleCandidate> Candidates { get; } = [];

    public TitleCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (_selectedCandidate == value) return;
            _selectedCandidate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionDisplay));
        }
    }

    public string SelectionDisplay => SelectedCandidate?.DisplayName ?? "Choose a SubDL title…";

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
