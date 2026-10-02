using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubdlProDownload.Configuration;
using SubdlProDownload.Models;

namespace SubdlProDownload.Services;

/// <summary>SubDL v2 client with release matching followed by selected-title fallbacks.</summary>
public sealed class SubdlProClient : ISubtitleProvider
{
    private const double ExactReleaseThreshold = 0.80;
    private static readonly Uri ApiBase = new("https://api.subdl.com/api/v2/");
    private readonly AppSettings _settings;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(45) };

    public SubdlProClient(AppSettings settings) => _settings = settings;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_settings.HasApiKey)
            throw new InvalidOperationException("Paste your SubDL Pro API key into the field above, then start the search.");
        return VerifyCredentialsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TitleCandidate>> SearchTitlesAsync(string query, CancellationToken cancellationToken)
    {
        var uri = new Uri(ApiBase, $"movies/search?q={Uri.EscapeDataString(query)}&limit=10");
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return [];

        var candidates = new List<TitleCandidate>();
        foreach (var result in results.EnumerateArray())
        {
            var id = GetIdentifier(result, "sd_id");
            var name = GetString(result, "name") ?? GetString(result, "original_name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            candidates.Add(new TitleCandidate(
                id,
                name,
                GetIdentifier(result, "year"),
                GetString(result, "type") ?? "title",
                GetString(result, "imdb_id")));
        }
        return candidates;
    }

    public async Task<SubtitleLookup> FindBestMatchAsync(
        VideoItem video,
        TitleCandidate? selectedTitle,
        string language,
        CancellationToken cancellationToken)
    {
        var releaseMatch = await FindExactReleaseMatchAsync(video, language, cancellationToken);
        if (releaseMatch is not null)
            return new SubtitleLookup(releaseMatch, "Exact release match");

        if (selectedTitle is null)
        {
            return new SubtitleLookup(
                null,
                "No exact release match. Choose a SubDL title above to enable episode fallback.");
        }

        var episodeMatch = await FindByTitleAsync(video, selectedTitle, language, seasonPack: false, cancellationToken);
        if (episodeMatch is not null)
            return new SubtitleLookup(episodeMatch, "Episode fallback");

        if (video.Identity.Season is not null && video.Identity.Episode is not null)
        {
            var seasonPackMatch = await FindByTitleAsync(video, selectedTitle, language, seasonPack: true, cancellationToken);
            if (seasonPackMatch is not null)
                return new SubtitleLookup(seasonPackMatch, "Season-pack fallback");
        }

        return new SubtitleLookup(null, $"No English SubDL subtitle found for {selectedTitle.DisplayName} / {video.Identity.EpisodeDisplay}.");
    }

    public async Task DownloadAsync(SubtitleMatch match, string destinationPath, CancellationToken cancellationToken)
    {
        var format = match.MatchKind == "Season pack" ? "zip" : "file";
        var downloadUri = new Uri(ApiBase, $"subtitles/{Uri.EscapeDataString(match.SubtitleId)}/download?format={format}");
        using var request = CreateRequest(HttpMethod.Get, downloadUri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = destinationPath + ".part";
        try
        {
            if (IsZip(bytes))
                await WriteSrtFromZipAsync(bytes, temporaryPath, match, cancellationToken);
            else
                await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<SubtitleMatch?> FindExactReleaseMatchAsync(VideoItem video, string language, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(video.FullPath);
        var uri = new Uri(ApiBase,
            $"files/search?filename={Uri.EscapeDataString(fileName)}&languages={Uri.EscapeDataString(language)}&engine=auto&episode_scope=exact&hi=0&subs_per_page=30");
        var matches = await SearchSubtitleMatchesAsync(uri, video, "Exact release", cancellationToken);
        return matches.OrderByDescending(match => match.Score).FirstOrDefault(match => match.Score >= ExactReleaseThreshold);
    }

    private async Task<SubtitleMatch?> FindByTitleAsync(
        VideoItem video,
        TitleCandidate selectedTitle,
        string language,
        bool seasonPack,
        CancellationToken cancellationToken)
    {
        var parameters = new List<string>
        {
            $"sd_id={Uri.EscapeDataString(selectedTitle.SubdlId)}",
            $"languages={Uri.EscapeDataString(language)}",
            "unpack=1"
        };

        if (video.Identity.Season is not null)
            parameters.Add($"season={video.Identity.Season}");
        if (!seasonPack && video.Identity.Episode is not null)
            parameters.Add($"episode={video.Identity.Episode}");
        if (seasonPack)
            parameters.Add("full_season=1");

        var uri = new Uri(ApiBase, "subtitles/search?" + string.Join("&", parameters));
        var kind = seasonPack ? "Season pack" : video.Identity.IsEpisode ? "Episode fallback" : "Title fallback";
        var matches = await SearchSubtitleMatchesAsync(uri, video, kind, cancellationToken);

        return matches.OrderByDescending(match => match.Score).FirstOrDefault();
    }

    private async Task<List<SubtitleMatch>> SearchSubtitleMatchesAsync(Uri uri, VideoItem video, string matchKind, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array)
            return [];

        var matches = new List<SubtitleMatch>();
        foreach (var subtitle in subtitles.EnumerateArray())
        {
            var subtitleId = GetIdentifier(subtitle, "n_id") ?? GetIdentifier(subtitle, "nId");
            if (string.IsNullOrWhiteSpace(subtitleId)) continue;
            var releaseName = GetString(subtitle, "release_name");
            var sourceFileName = GetString(subtitle, "name") ?? GetString(subtitle, "file_name");
            var score = GetDouble(subtitle, "match_score") ?? ReleaseSimilarity(Path.GetFileNameWithoutExtension(video.FileName), releaseName ?? sourceFileName);
            matches.Add(new SubtitleMatch(
                "SubDL Pro",
                subtitleId,
                sourceFileName,
                score,
                releaseName,
                matchKind,
                video.Identity.Season,
                video.Identity.Episode));
        }
        return matches;
    }

    private async Task VerifyCredentialsAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(ApiBase, "me"));
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("SubDL rejected the saved API key. Paste a new key into the field and retry.");
        throw new HttpRequestException($"SubDL account check returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("SubDL quota reached. Check the quota indicator in your SubDL account.", null, response.StatusCode);
        throw new HttpRequestException($"SubDL returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }

    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static async Task WriteSrtFromZipAsync(byte[] archiveBytes, string destination, SubtitleMatch match, CancellationToken cancellationToken)
    {
        await using var input = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var srtEntries = archive.Entries.Where(entry => entry.FullName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (srtEntries.Length == 0) throw new InvalidOperationException("SubDL downloaded a ZIP without an SRT file.");

        var srt = SelectSrtEntry(srtEntries, match)
            ?? throw new InvalidOperationException("The season pack did not expose a safe SRT match for this episode.");
        await using var source = srt.Open();
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static ZipArchiveEntry? SelectSrtEntry(IReadOnlyCollection<ZipArchiveEntry> entries, SubtitleMatch match)
    {
        if (entries.Count == 1) return entries.First();
        if (match.ExpectedEpisode is null) return entries.OrderBy(entry => entry.FullName.Length).First();

        var ranked = entries
            .Select(entry => new { Entry = entry, Score = EpisodeEntryScore(entry.FullName, match.ExpectedSeason, match.ExpectedEpisode.Value) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.FullName.Length)
            .First();
        return ranked.Score > 0 ? ranked.Entry : null;
    }

    private static int EpisodeEntryScore(string path, int? season, int episode)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (season is not null && Regex.IsMatch(name, $@"s0?{season}[^0-9]*e0?{episode}(?!\d)|0?{season}x0?{episode}(?!\d)", RegexOptions.IgnoreCase)) return 100;
        if (season is not null && Regex.IsMatch(name, $@"(?<!\d){season:00}{episode:00}(?!\d)")) return 85;
        if (Regex.IsMatch(name, $@"(?:episode|ep)[ ._-]*0?{episode}(?!\d)", RegexOptions.IgnoreCase)) return 70;
        if (Regex.IsMatch(name, $@"(?:^|[^0-9])0?{episode}(?:$|[^0-9])")) return 40;
        return 0;
    }

    private static double ReleaseSimilarity(string fileName, string? releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName)) return 0.50;
        var left = Tokenize(fileName);
        var right = Tokenize(releaseName);
        if (left.Count == 0 || right.Count == 0) return 0.50;
        return (double)left.Intersect(right, StringComparer.OrdinalIgnoreCase).Count() / Math.Max(left.Count, right.Count);
    }

    private static string[] Tokenize(string value) => Regex.Split(value, @"[^A-Za-z0-9]+")
        .Where(token => token.Length > 1)
        .ToArray();
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? GetIdentifier(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
    private static double? GetDouble(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
}
