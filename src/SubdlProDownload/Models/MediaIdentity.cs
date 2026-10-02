namespace SubdlProDownload.Models;

public sealed record MediaIdentity(
    string GroupKey,
    string Title,
    int? Season,
    int? Episode,
    string Evidence)
{
    public bool IsEpisode => Season is not null && Episode is not null;

    public string EpisodeDisplay => IsEpisode
        ? $"S{Season:00}E{Episode:00}"
        : "Film / unknown episode";
}
