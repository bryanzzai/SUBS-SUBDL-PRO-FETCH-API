using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using SubdlProDownload.Configuration;
using SubdlProDownload.Models;
using SubdlProDownload.Services;

namespace SubdlProDownload;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _operationCts;
    public string ReleaseLabel => $"Bulk {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save the bulk subtitle ZIP files", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(OutputFolderTextBox.Text) && Directory.Exists(OutputFolderTextBox.Text))
            dialog.InitialDirectory = OutputFolderTextBox.Text;
        if (dialog.ShowDialog(this) == true)
            OutputFolderTextBox.Text = dialog.FolderName;
    }

    private async void RunBulkButton_Click(object sender, RoutedEventArgs e)
    {
        var queries = SeriesListTextBox.Text
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (queries.Length is < 1 or > 10)
        {
            ShowInfo("Enter between 1 and 10 TV-series titles, one per line.", "Bulk input");
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            ShowInfo("Choose an output folder first.", "Bulk destination");
            return;
        }

        if (!TryGetSettings(out var settings, out var savingNewKey)) return;

        BeginOperation("Checking SubDL account and quota…");
        LogTextBox.Clear();
        ProgressBar.Maximum = queries.Length * (SubdlProClient.SeasonSearchLimit + 1);
        ProgressBar.Value = 0;

        try
        {
            Directory.CreateDirectory(outputFolder);
            await using var client = new SubdlProClient(settings);
            await client.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);

            var before = await client.GetAccountUsageAsync(_operationCts.Token);
            var plannedSearchCalls = queries.Length * (SubdlProClient.SeasonSearchLimit + 1);
            AppendLog($"Plan: {before.PlanName}  Search remaining: {before.SearchRemaining}/{before.SearchLimit}  Downloads remaining: {before.DownloadsRemaining}/{before.DownloadsLimit}");
            AppendLog($"Planned metadata bill: {plannedSearchCalls} search request(s) for {queries.Length} series.");

            if (before.SearchRemaining < plannedSearchCalls)
                throw new InvalidOperationException($"Full-load preflight failed: need {plannedSearchCalls} search requests, but SubDL reports only {before.SearchRemaining} remaining. Nothing was scanned or downloaded.");

            var results = new List<BulkSeriesResult>();
            var completedSearchCalls = 0;

            foreach (var query in queries)
            {
                _operationCts.Token.ThrowIfCancellationRequested();
                StatusTextBlock.Text = $"Resolving {query}…";
                AppendLog($"\n[{query}] title lookup");

                var candidates = (await client.SearchTitlesAsync(query, _operationCts.Token))
                    .Where(candidate => candidate.IsTvSeries)
                    .ToArray();
                completedSearchCalls++;
                ProgressBar.Value = completedSearchCalls;

                var exactMatches = candidates
                    .Where(candidate => string.Equals(candidate.Name, query, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                TitleCandidate title;
                if (exactMatches.Length == 1)
                {
                    title = exactMatches[0];
                }
                else if (candidates.Length == 1)
                {
                    title = candidates[0];
                }
                else
                {
                    var options = candidates.Length == 0
                        ? "no TV results"
                        : string.Join(" | ", candidates.Take(10).Select(candidate => candidate.DisplayName));
                    throw new InvalidOperationException($"Ambiguous or unresolved title '{query}': {options}. Bulk run aborted before download.");
                }

                AppendLog($"[{query}] resolved -> {title.DisplayName} (sd_id {title.SubdlId})");

                var titleBaseCalls = completedSearchCalls;
                var progress = new Progress<RawSeasonSearchProgress>(update =>
                {
                    ProgressBar.Value = Math.Min(ProgressBar.Maximum, titleBaseCalls + update.SeasonsCompleted);
                    StatusTextBlock.Text = $"{title.Name}: season {update.SeasonNumber}/{update.TotalSeasons}";
                });

                var rows = await client.SearchRawSeasonResultsAsync(title, progress, _operationCts.Token);
                completedSearchCalls += SubdlProClient.SeasonSearchLimit;
                ProgressBar.Value = completedSearchCalls;
                var rawCount = rows.Count(row => row.IsRawRow);
                var downloadableCount = rows.Count(row => row.IsRawRow && row.HasDownloadUrl);
                AppendLog($"[{query}] metadata rows: {rawCount}; downloadable ZIP rows: {downloadableCount}");
                results.Add(new BulkSeriesResult(query, title, rows));
            }

            var diagnosticRows = results.SelectMany(result => result.Rows).Where(row => !row.IsRawRow).ToArray();
            var beforeDownload = await client.GetAccountUsageAsync(_operationCts.Token);
            var downloadRows = results
                .SelectMany(result => result.Rows.Where(row => row.IsRawRow && row.HasDownloadUrl).Select(row => new BulkDownloadItem(result, row)))
                .ToArray();

            AppendLog($"\nMetadata phase complete. Search requests used by this run: {completedSearchCalls}.");
            AppendLog($"Full download bill: {downloadRows.Length} ZIP download(s).");
            AppendLog($"Quota now: search {beforeDownload.SearchRemaining}/{beforeDownload.SearchLimit} remaining; downloads {beforeDownload.DownloadsRemaining}/{beforeDownload.DownloadsLimit} remaining.");

            var manifestPath = Path.Combine(outputFolder, $"subdl-bulk-manifest-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await WriteManifestAsync(manifestPath, queries, results, before, beforeDownload, null, completedSearchCalls, 0, 0, _operationCts.Token);
            AppendLog($"Metadata manifest written: {manifestPath}");

            if (diagnosticRows.Length > 0)
                throw new InvalidOperationException($"Metadata scan returned {diagnosticRows.Length} diagnostic/error row(s). Full-load rule stops before download. Inspect the manifest.");

            if (beforeDownload.DownloadsRemaining < downloadRows.Length)
                throw new InvalidOperationException($"Full-load preflight failed: need {downloadRows.Length} downloads, but SubDL reports only {beforeDownload.DownloadsRemaining} remaining. Metadata is saved; no ZIP downloads were started.");

            ProgressBar.Maximum = Math.Max(1, downloadRows.Length);
            ProgressBar.Value = 0;
            var attempted = 0;
            var failed = 0;

            foreach (var item in downloadRows)
            {
                _operationCts.Token.ThrowIfCancellationRequested();
                attempted++;
                var seriesFolder = Path.Combine(outputFolder, SanitizePathPart(item.Series.Title.Name));
                Directory.CreateDirectory(seriesFolder);
                var destination = Path.Combine(seriesFolder, BuildArchiveName(item.Series.Title.Name, item.Row));
                item.LocalPath = destination;
                StatusTextBlock.Text = $"Downloading {attempted}/{downloadRows.Length}: {item.Series.Title.Name}";
                item.Row.DownloadStatus = "Downloading ZIP…";

                try
                {
                    await client.DownloadReturnedUrlAsync(item.Row.DownloadUrl, destination, _operationCts.Token);
                    item.Row.DownloadStatus = "Saved ZIP";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    item.Row.DownloadStatus = "Failed: " + ex.Message;
                    AppendLog($"DOWNLOAD FAILED: {item.Series.Title.Name} / {item.Row.ReleaseName}: {ex.Message}");
                }

                ProgressBar.Value = attempted;
            }

            var after = await client.GetAccountUsageAsync(_operationCts.Token);
            await WriteManifestAsync(manifestPath, queries, results, before, beforeDownload, after, completedSearchCalls, attempted, failed, _operationCts.Token, downloadRows);

            AppendLog($"\nDONE. Search calls: {completedSearchCalls}. Downloads attempted: {attempted}. Saved: {attempted - failed}. Failed: {failed}.");
            AppendLog($"Quota after: search {after.SearchRemaining}/{after.SearchLimit}; downloads {after.DownloadsRemaining}/{after.DownloadsLimit}.");
            StatusTextBlock.Text = failed == 0
                ? $"Bulk load complete: {attempted} ZIP file(s) saved."
                : $"Bulk load complete with {failed} failed download(s). See log and manifest.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Bulk run cancelled.";
            AppendLog("\nCANCELLED.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Bulk run stopped.";
            AppendLog("\nSTOPPED: " + ex.Message);
            MessageBox.Show(this, ex.Message, "SubDL Bulk Download", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndOperation();
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        IReadOnlyList<string> queries,
        IReadOnlyList<BulkSeriesResult> results,
        SubdlAccountUsage before,
        SubdlAccountUsage beforeDownload,
        SubdlAccountUsage? after,
        int searchCalls,
        int downloadAttempts,
        int failedDownloads,
        CancellationToken cancellationToken,
        IReadOnlyList<BulkDownloadItem>? downloadItems = null)
    {
        var localPaths = (downloadItems ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.LocalPath))
            .ToDictionary(item => item.Row, item => item.LocalPath!);

        var manifest = new
        {
            generated_at = DateTimeOffset.Now,
            input_series = queries,
            bill = new
            {
                search_requests = searchCalls,
                download_candidates = results.SelectMany(result => result.Rows).Count(row => row.IsRawRow && row.HasDownloadUrl),
                download_attempts = downloadAttempts,
                download_failures = failedDownloads
            },
            quota_before = before,
            quota_before_download = beforeDownload,
            quota_after = after,
            series = results.Select(result => new
            {
                input = result.Query,
                resolved = new { result.Title.Name, result.Title.Year, result.Title.SubdlId, result.Title.ImdbId },
                rows = result.Rows.Select(row => new
                {
                    row.QuerySeason,
                    row.RowNumber,
                    row.Kind,
                    row.HttpStatus,
                    row.ReleaseName,
                    row.SourceName,
                    row.SeasonValue,
                    row.EpisodeValue,
                    hi = row.IsHearingImpaired,
                    row.PackageId,
                    row.SubtitlePage,
                    download_url = row.DownloadUrlDisplay,
                    row.DownloadStatus,
                    row.Details,
                    row.RawJson,
                    local_zip_path = localPaths.TryGetValue(row, out var localPath) ? localPath : null
                })
            })
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private static string BuildArchiveName(string titleName, RawSubtitleRow row)
    {
        var release = row.ReleaseName != "—" ? row.ReleaseName : row.SourceName;
        var usefulRelease = string.IsNullOrWhiteSpace(release) || release == "—" ? "subtitle" : release;
        var identity = string.IsNullOrWhiteSpace(row.PackageId) || row.PackageId == "—" ? $"row-{row.QuerySeason:00}-{row.RowNumber:000}" : row.PackageId;
        var season = row.SeasonValue != "—" ? row.SeasonValue.PadLeft(2, '0') : row.QuerySeason.ToString("00");
        var name = $"{titleName} S{season} - {usefulRelease} - {identity}.zip";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return sanitized.Length <= 180 ? sanitized : sanitized[..176] + ".zip";
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Unknown series" : sanitized;
    }

    private bool TryGetSettings(out AppSettings settings, out bool savingNewKey)
    {
        var enteredKey = ApiKeyTextBox.Text.Trim();
        savingNewKey = !string.IsNullOrWhiteSpace(enteredKey);
        settings = savingNewKey ? new AppSettings(enteredKey) : AppSettings.LoadSaved();
        if (settings.HasApiKey) return true;
        ShowInfo("Paste your SubDL Pro API key into the field first. Leave it blank on later runs to use the saved key.", "SubDL Pro API key");
        return false;
    }

    private void SaveVerifiedKeyIfNeeded(AppSettings settings, bool savingNewKey)
    {
        if (!savingNewKey) return;
        settings.Save();
        ApiKeyTextBox.Clear();
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText(message + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private void BeginOperation(string status)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        RunBulkButton.IsEnabled = false;
        BrowseOutputButton.IsEnabled = false;
        SeriesListTextBox.IsEnabled = false;
        OutputFolderTextBox.IsEnabled = false;
        ApiKeyTextBox.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = status;
    }

    private void EndOperation()
    {
        RunBulkButton.IsEnabled = true;
        BrowseOutputButton.IsEnabled = true;
        SeriesListTextBox.IsEnabled = true;
        OutputFolderTextBox.IsEnabled = true;
        ApiKeyTextBox.IsEnabled = true;
        CancelButton.IsEnabled = false;
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void ShowInfo(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private sealed record BulkSeriesResult(string Query, TitleCandidate Title, IReadOnlyList<RawSubtitleRow> Rows);

    private sealed class BulkDownloadItem
    {
        public BulkDownloadItem(BulkSeriesResult series, RawSubtitleRow row)
        {
            Series = series;
            Row = row;
        }

        public BulkSeriesResult Series { get; }
        public RawSubtitleRow Row { get; }
        public string? LocalPath { get; set; }
    }
}
