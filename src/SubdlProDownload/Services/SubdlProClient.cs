using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SubdlProDownload.Configuration;

namespace SubdlProDownload.Services;

/// <summary>
/// Uses SubDL's release-filename endpoint. It deliberately declines weak
/// matches rather than silently saving a subtitle for a different cut/episode.
/// </summary>
public sealed class SubdlProClient : ISubtitleProvider
{
    private const double AutomaticMatchThreshold = 0.80;
    private static readonly Uri ApiBase = new("https://api.subdl.com/api/v2/");
    private readonly AppSettings _settings;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(45) };

    public SubdlProClient(AppSettings settings) => _settings = settings;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_settings.HasApiKey)
            throw new InvalidOperationException("Paste your SubDL Pro API key into the field above, then start the download.");
        return VerifyCredentialsAsync(cancellationToken);
    }

    public async Task<SubtitleLookup> FindBestMatchAsync(string videoPath, string language, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(videoPath);
        var requestUri = new Uri(ApiBase,
            $"files/search?filename={Uri.EscapeDataString(fileName)}&languages={Uri.EscapeDataString(language)}&engine=auto&episode_scope=exact&hi=0&subs_per_page=30");

        using var request = CreateRequest(HttpMethod.Get, requestUri);
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array)
            return new SubtitleLookup(null, "SubDL returned no English subtitle candidates.");

        var matches = new List<SubtitleMatch>();
        foreach (var subtitle in subtitles.EnumerateArray())
        {
            var subtitleId = GetIdentifier(subtitle, "n_id") ?? GetIdentifier(subtitle, "nId");
            if (string.IsNullOrWhiteSpace(subtitleId)) continue;
            var score = GetDouble(subtitle, "match_score") ?? 0;
            matches.Add(new SubtitleMatch(
                "SubDL Pro",
                subtitleId,
                GetString(subtitle, "name") ?? GetString(subtitle, "file_name"),
                score,
                GetString(subtitle, "release_name")));
        }

        var best = matches.OrderByDescending(match => match.Score).FirstOrDefault();
        if (best is null)
            return new SubtitleLookup(null, "SubDL returned no downloadable English subtitle candidates.");

        if (best.Score < AutomaticMatchThreshold)
        {
            return new SubtitleLookup(
                null,
                $"Ambiguous SubDL match ({best.Score:0.00}); skipped to avoid a wrong episode or release.");
        }

        return new SubtitleLookup(best, $"SubDL match {best.Score:0.00}");
    }

    public async Task DownloadAsync(SubtitleMatch match, string destinationPath, CancellationToken cancellationToken)
    {
        var downloadUri = new Uri(ApiBase, $"subtitles/{Uri.EscapeDataString(match.SubtitleId)}/download?format=file");
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
                await WriteSrtFromZipAsync(bytes, temporaryPath, cancellationToken);
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

    private static async Task WriteSrtFromZipAsync(byte[] archiveBytes, string destination, CancellationToken cancellationToken)
    {
        await using var input = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var srt = archive.Entries
            .Where(entry => entry.FullName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("SubDL downloaded a ZIP without an SRT file.");
        await using var source = srt.Open();
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static string? GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? GetIdentifier(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;
    private static double? GetDouble(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
}
