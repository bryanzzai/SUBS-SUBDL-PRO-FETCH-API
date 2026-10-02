namespace SubdlProDownload.Models;

public sealed record TitleCandidate(
    string SubdlId,
    string Name,
    string? Year,
    string Type,
    string? ImdbId)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Year)
        ? $"{Name} ({Type})"
        : $"{Name} ({Year}, {Type})";
}
