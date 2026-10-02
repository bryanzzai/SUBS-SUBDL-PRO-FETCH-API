using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubdlProDownload.Configuration;
using SubdlProDownload.Models;

namespace SubdlProDownload.Services;

/// <summary>Small, direct client for the SubDL title, season-list and ZIP endpoints.</summary>
public sealed class SubdlProClient : IAsyncDisposable
{
    public const int SeasonSearchLimit = 15;
    private static readonly Uri ApiBase = new("https://api.subdl.com/api/v2/");
    private readonly AppSettings _settings;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(60) };

    public SubdlProClient(AppSettings settings) => _settings = settings;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_settings.HasApiKey)
            throw new InvalidOperationException("Paste your SubDL Pro API key into the field above, then start the search.");
        return VerifyCredentialsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TitleCandidate>> SearchTitlesAsync(string query, CancellationToken cancellationToken)
    {
        var uri = new Uri(ApiBase, $"movies/search?q={Uri.EscapeDataString(query)}&type=tv&limit=20");
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
                GetString(result, "type") ?? "tv",
                GetString(result, "imdb_id")));
        }
        return candidates;
    }

    public async Task<IReadOnlyList<SeasonPackItem>> SearchSeasonPacksAsync(
        TitleCandidate title,
        IProgress<SeasonSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var packs = new List<SeasonPackItem>();
        for (var season = 1; season <= SeasonSearchLimit; season++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mask = BuildSeasonMask(title.Name, season);
            progress?.Report(new SeasonSearchProgress(season, season - 1, SeasonSearchLimit, mask, null));

            // This intentionally asks for the season's normal subtitle list, not full_season=1.
            // Many actual season archives are filed there under names such as justified.s01.
            var uri = new Uri(ApiBase,
                $"subtitles/search?sd_id={Uri.EscapeDataString(title.SubdlId)}&languages=en&season={season}");
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var matchesThisSeason = 0;
            if (document.RootElement.TryGetProperty("subtitles", out var subtitles) && subtitles.ValueKind == JsonValueKind.Array)
            {
                foreach (var subtitle in subtitles.EnumerateArray())
                {
                    var subtitleId = GetIdentifier(subtitle, "n_id") ?? GetIdentifier(subtitle, "nId");
                    if (string.IsNullOrWhiteSpace(subtitleId)) continue;
                    var releaseName = GetString(subtitle, "release_name");
                    var sourceFileName = GetString(subtitle, "name") ?? GetString(subtitle, "file_name");
                    var packageName = releaseName ?? sourceFileName ?? $"SubDL package {subtitleId}";
                    if (!MatchesSeasonMask(title.Name, season, packageName, sourceFileName)) continue;

                    matchesThisSeason++;
                    packs.Add(new SeasonPackItem(subtitleId, $"Season {season}", packageName, sourceFileName));
                }
            }
            if (matchesThisSeason == 0)
                packs.Add(SeasonPackItem.NotFound(season, mask));
            progress?.Report(new SeasonSearchProgress(season, season, SeasonSearchLimit, mask, matchesThisSeason));
        }

        return packs
            .GroupBy(pack => pack.SubtitleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(pack => SeasonSortKey(pack.SeasonLabel))
            .ThenBy(pack => pack.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string BuildSeasonMask(string title, int season) =>
        $"{NormalizeTitleForMask(title)}.s{season:00}.";

    public async Task DownloadSeasonPackAsync(SeasonPackItem pack, string destinationPath, CancellationToken cancellationToken)
    {
        var uri = new Uri(ApiBase, $"subtitles/{Uri.EscapeDataString(pack.SubtitleId)}/download?format=zip");
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!IsZip(bytes))
            throw new InvalidOperationException("SubDL did not return a ZIP file for this season package.");

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporaryPath = destinationPath + ".part";
        try
        {
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

    private static string GetSeasonLabel(JsonElement subtitle, string name)
    {
        var number = GetIdentifier(subtitle, "season") ?? GetIdentifier(subtitle, "season_number");
        if (int.TryParse(number, out var explicitSeason)) return $"Season {explicitSeason}";

        var match = Regex.Match(name, @"(?:^|[. _-])S(?:eason[. _-]*)?0?(?<season>\d{1,2})(?:[. _-]|$)|\bSeason[. _-]*(?<season>\d{1,2})\b", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["season"].Value, out var parsedSeason)
            ? $"Season {parsedSeason}"
            : "Season ?";
    }

    private static int SeasonSortKey(string label)
    {
        var match = Regex.Match(label, @"\d+");
        return match.Success && int.TryParse(match.Value, out var number) ? number : int.MaxValue;
    }

    private static bool MatchesSeasonMask(string title, int season, string packageName, string? sourceFileName)
    {
        var titleWords = Regex.Matches(title, @"[\p{L}\p{N}]+")
            .Select(match => Regex.Escape(match.Value))
            .ToArray();
        if (titleWords.Length == 0) return false;

        var titlePattern = string.Join(@"[. _-]+", titleWords);
        var pattern = $@"(?<![\p{{L}}\p{{N}}]){titlePattern}[. _-]+s0?{season}(?=[. _-])";
        return Regex.IsMatch(packageName, pattern, RegexOptions.IgnoreCase)
            || (!string.IsNullOrWhiteSpace(sourceFileName)
                && Regex.IsMatch(sourceFileName, pattern, RegexOptions.IgnoreCase));
    }

    private static string NormalizeTitleForMask(string title) => string.Join('.',
        Regex.Matches(title, @"[\p{L}\p{N}]+")
            .Select(match => match.Value.ToLowerInvariant()));

    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? GetIdentifier(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
}

public sealed record SeasonSearchProgress(
    int SeasonNumber,
    int SeasonsCompleted,
    int TotalSeasons,
    string Mask,
    int? MatchesFound);
