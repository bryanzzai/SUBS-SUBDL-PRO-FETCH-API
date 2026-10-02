using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubdlProDownload.Configuration;
using SubdlProDownload.Models;

namespace SubdlProDownload.Services;

/// <summary>Direct SubDL client for title lookup, raw season monitoring and returned-URL ZIP downloads.</summary>
public sealed class SubdlProClient : IAsyncDisposable
{
    public const int SeasonSearchLimit = 15;
    public const int RawRowsPerSeasonLimit = 100;

    private static readonly Uri ApiBase = new("https://api.subdl.com/api/v2/");
    private static readonly Uri DownloadBase = new("https://api.subdl.com/");
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

    public async Task<IReadOnlyList<RawSubtitleRow>> SearchRawSeasonResultsAsync(
        TitleCandidate title,
        IProgress<RawSeasonSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rows = new List<RawSubtitleRow>();

        for (var season = 1; season <= SeasonSearchLimit; season++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new RawSeasonSearchProgress(season, season - 1, SeasonSearchLimit, null));

            var uri = new Uri(ApiBase,
                $"subtitles/search?sd_id={Uri.EscapeDataString(title.SubdlId)}&languages=en&season={season}");
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var httpStatus = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();

            if (!response.IsSuccessStatusCode)
            {
                rows.Add(DiagnosticRow(
                    season,
                    "HTTP ERROR",
                    httpStatus,
                    $"SubDL returned a non-success response for requested season {season}.",
                    Truncate(MaskApiKey(body), 1200)));
                progress?.Report(new RawSeasonSearchProgress(season, season, SeasonSearchLimit, 0));
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                rows.Add(DiagnosticRow(
                    season,
                    "INVALID JSON",
                    httpStatus,
                    $"Season {season}: {ex.Message}",
                    Truncate(MaskApiKey(body), 1200)));
                progress?.Report(new RawSeasonSearchProgress(season, season, SeasonSearchLimit, 0));
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array)
                {
                    var rootProperties = root.ValueKind == JsonValueKind.Object
                        ? string.Join(", ", root.EnumerateObject().Select(property => property.Name))
                        : $"root kind: {root.ValueKind}";
                    rows.Add(DiagnosticRow(
                        season,
                        "NO subtitles[]",
                        httpStatus,
                        $"Season {season}: expected subtitles array was not present. Root properties: {rootProperties}",
                        Truncate(MaskApiKey(body), 1200)));
                    progress?.Report(new RawSeasonSearchProgress(season, season, SeasonSearchLimit, 0));
                    continue;
                }

                var apiRowCount = subtitles.GetArrayLength();
                var index = 0;
                foreach (var subtitle in subtitles.EnumerateArray())
                {
                    index++;
                    if (index > RawRowsPerSeasonLimit) break;

                    var subtitlePage = GetString(subtitle, "subtitlePage") ?? "—";
                    var packageId = ExtractPackageId(subtitlePage)
                        ?? GetIdentifier(subtitle, "n_id")
                        ?? GetIdentifier(subtitle, "nId")
                        ?? GetIdentifier(subtitle, "id")
                        ?? "—";
                    var downloadUrl = GetString(subtitle, "url") ?? "—";
                    var isHearingImpaired = subtitle.TryGetProperty("hi", out var hiValue)
                        && hiValue.ValueKind == JsonValueKind.True;

                    rows.Add(new RawSubtitleRow(
                        season,
                        index,
                        "RAW",
                        httpStatus,
                        packageId,
                        subtitlePage,
                        downloadUrl,
                        MaskApiKey(downloadUrl),
                        GetString(subtitle, "release_name") ?? "—",
                        GetString(subtitle, "name") ?? GetString(subtitle, "file_name") ?? "—",
                        GetIdentifier(subtitle, "season") ?? GetIdentifier(subtitle, "season_number") ?? season.ToString(),
                        GetIdentifier(subtitle, "episode") ?? GetIdentifier(subtitle, "episode_number") ?? "—",
                        isHearingImpaired,
                        BuildKnownFieldSummary(subtitle),
                        Truncate(MaskApiKey(subtitle.GetRawText()), 1200)));
                }

                progress?.Report(new RawSeasonSearchProgress(season, season, SeasonSearchLimit, apiRowCount));
            }
        }

        return rows;
    }

    public async Task DownloadReturnedUrlAsync(string downloadUrl, string destinationPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl) || downloadUrl == "—")
            throw new InvalidOperationException("SubDL did not return a download URL for this row.");

        Uri uri;
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var absoluteUri))
            uri = absoluteUri;
        else if (Uri.TryCreate(DownloadBase, downloadUrl, out var relativeUri))
            uri = relativeUri;
        else
            throw new InvalidOperationException("SubDL returned a download URL that could not be parsed.");

        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!IsZip(bytes))
            throw new InvalidOperationException("SubDL download URL did not return a ZIP file.");

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

    private static RawSubtitleRow DiagnosticRow(int season, string kind, string httpStatus, string details, string rawJson) =>
        new(
            season,
            0,
            kind,
            httpStatus,
            "—",
            "—",
            "—",
            "—",
            "—",
            "—",
            season.ToString(),
            "—",
            false,
            details,
            rawJson,
            "—");

    private async Task VerifyCredentialsAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, new Uri(ApiBase, "me"));
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("SubDL rejected the saved API key. Paste a new key into the field and retry.");

        throw new HttpRequestException(
            $"SubDL account check returned {(int)response.StatusCode} {response.ReasonPhrase}: {MaskApiKey(body)}",
            null,
            response.StatusCode);
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

        throw new HttpRequestException(
            $"SubDL returned {(int)response.StatusCode} {response.ReasonPhrase}: {MaskApiKey(body)}",
            null,
            response.StatusCode);
    }

    private static string BuildKnownFieldSummary(JsonElement subtitle)
    {
        if (subtitle.ValueKind != JsonValueKind.Object) return $"JSON kind: {subtitle.ValueKind}";
        var names = subtitle.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == 0 ? "No object fields" : "Fields: " + string.Join(", ", names);
    }

    private static string? ExtractPackageId(string subtitlePage)
    {
        if (string.IsNullOrWhiteSpace(subtitlePage) || subtitlePage == "—") return null;
        var match = Regex.Match(subtitlePage, @"(?:^|/)s/info/(?<id>[^/?#]+)", RegexOptions.IgnoreCase);
        return match.Success ? Uri.UnescapeDataString(match.Groups["id"].Value) : null;
    }

    private static string MaskApiKey(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return Regex.Replace(value, "(?<=api_key=)[^&\\\"'\\s}]+", "***", RegexOptions.IgnoreCase);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetIdentifier(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
}

public sealed record RawSeasonSearchProgress(
    int SeasonNumber,
    int SeasonsCompleted,
    int TotalSeasons,
    int? ApiRowsFound);
