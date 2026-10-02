using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubdlProDownload.Configuration;
using SubdlProDownload.Models;

namespace SubdlProDownload.Services;

/// <summary>Small, direct client for the SubDL title, season-pack and ZIP endpoints.</summary>
public sealed class SubdlProClient : IAsyncDisposable
{
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

    public async Task<IReadOnlyList<SeasonPackItem>> SearchSeasonPacksAsync(TitleCandidate title, CancellationToken cancellationToken)
    {
        // full_season=1 deliberately asks for the packages, not individual episode subtitle files.
        var uri = new Uri(ApiBase,
            $"subtitles/search?sd_id={Uri.EscapeDataString(title.SubdlId)}&languages=en&full_season=1");
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array)
            return [];

        var packs = new List<SeasonPackItem>();
        foreach (var subtitle in subtitles.EnumerateArray())
        {
            var subtitleId = GetIdentifier(subtitle, "n_id") ?? GetIdentifier(subtitle, "nId");
            if (string.IsNullOrWhiteSpace(subtitleId)) continue;
            var releaseName = GetString(subtitle, "release_name");
            var sourceFileName = GetString(subtitle, "name") ?? GetString(subtitle, "file_name");
            var packageName = releaseName ?? sourceFileName ?? $"SubDL package {subtitleId}";
            packs.Add(new SeasonPackItem(subtitleId, GetSeasonLabel(subtitle, packageName), packageName, sourceFileName));
        }

        return packs
            .GroupBy(pack => pack.SubtitleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(pack => SeasonSortKey(pack.SeasonLabel))
            .ThenBy(pack => pack.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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

    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;
    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? GetIdentifier(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
}
