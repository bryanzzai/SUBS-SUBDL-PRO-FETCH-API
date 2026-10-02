namespace SubdlProDownload.Models;

public sealed record TitleCandidate(
    string SubdlId,
    string Name,
    string? Year,
    string Type,
    string? ImdbId)
{
    public bool IsTvSeries => string.Equals(Type, "tv", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, "series", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Year)
        ? $"{Name} ({Type})"
        : $"{Name} ({Year}, {Type})";
}
