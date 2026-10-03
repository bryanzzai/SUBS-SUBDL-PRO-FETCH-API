using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SubdlProDownload.Models;

public sealed class BulkTitleChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    public BulkTitleChoice(string query, TitleCandidate candidate)
    {
        Query = query;
        Candidate = candidate;
    }

    public string Query { get; }
    public TitleCandidate Candidate { get; }
    public string Name => Candidate.Name;
    public string Year => Candidate.Year ?? "—";
    public string Type => Candidate.Type;
    public string SubdlId => Candidate.SubdlId;
    public string ImdbId => string.IsNullOrWhiteSpace(Candidate.ImdbId) ? "—" : Candidate.ImdbId;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
