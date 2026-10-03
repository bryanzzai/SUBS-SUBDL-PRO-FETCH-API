using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SubdlProDownload.Configuration;
using SubdlProDownload.Models;
using SubdlProDownload.Services;

namespace SubdlProDownload;

public partial class MainWindow : Window
{
    private const int MaxTitleResultRows = 200;
    private CancellationTokenSource? _operationCts;
    private int _lastTitleSearchCalls;

    public ObservableCollection<BulkTitleChoice> TitleChoices { get; } = [];
    public ICollectionView TitleChoicesView { get; private set; } = null!;
    public string ReleaseLabel => $"Bulk {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0"}";

    public MainWindow()
    {
        InitializeComponent();
        TitleChoicesView = CollectionViewSource.GetDefaultView(TitleChoices);
        TitleChoicesView.Filter = FilterTitleChoice;
        TitleChoicesView.SortDescriptions.Add(new SortDescription(nameof(BulkTitleChoice.Name), ListSortDirection.Ascending));
        DataContext = this;
    }

    private bool FilterTitleChoice(object item)
    {
        if (item is not BulkTitleChoice choice) return false;

        var filter = ResultsFilterTextBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(filter)) return true;

        return choice.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ResultsFilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        TitleChoicesView?.Refresh();
        UpdateResultCount();
    }

    private void UpdateResultCount()
    {
        var total = TitleChoices.Count;
        var visible = TitleChoicesView?.Cast<object>().Count(item => item is BulkTitleChoice) ?? total;
        var selected = TitleChoices.Count(choice => choice.IsSelected);

        CountTextBlock.Text = string.IsNullOrWhiteSpace(ResultsFilterTextBox?.Text)
            ? $"{total} result(s), {selected} selected"
            : $"{visible} of {total} result(s), {selected} selected";
    }

    private string[] ReadQueries() => SeriesListTextBox.Text
        .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save the bulk subtitle ZIP files", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(OutputFolderTextBox.Text) && Directory.Exists(OutputFolderTextBox.Text))
            dialog.InitialDirectory = OutputFolderTextBox.Text;
        if (dialog.ShowDialog(this) == true)
            OutputFolderTextBox.Text = dialog.FolderName;
    }

    private async void SearchTitlesButton_Click(object sender, RoutedEventArgs e)
    {
        var queries = ReadQueries();
        if (queries.Length is < 1 or > 10)
        {
            ShowInfo("Enter between 1 and 10 TV-series searches, one per line.", "Bulk input");
            return;
        }

        if (!TryGetSettings(out var settings, out var savingNewKey)) return;

        BeginOperation("Checking SubDL account and title-search quota…");
        TitleChoices.Clear();
        TitleChoicesView.Refresh();
        UpdateResultCount();
        ProgressBar.Maximum = queries.Length;
        ProgressBar.Value = 0;
        _lastTitleSearchCalls = 0;

        try
        {
            await using var client = new SubdlProClient(settings);
            await client.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);

            var usage = await client.GetAccountUsageAsync(_operationCts.Token);
            if (usage.SearchRemaining < queries.Length)
                throw new InvalidOperationException($"Title-search preflight failed: need {queries.Length} search request(s), but SubDL reports only {usage.SearchRemaining} remaining.");

            var capped = false;
            foreach (var query in queries)
            {
                _operationCts.Token.ThrowIfCancellationRequested();
                StatusTextBlock.Text = $"Searching SubDL: {query}";

                var candidates = await client.SearchTitlesAsync(query, _operationCts.Token);
                _lastTitleSearchCalls++;
                ProgressBar.Value = _lastTitleSearchCalls;

                foreach (var candidate in candidates.Where(candidate => candidate.IsTvSeries))
                {
                    if (TitleChoices.Count >= MaxTitleResultRows)
                    {
                        capped = true;
                        break;
                    }

                    TitleChoices.Add(new BulkTitleChoice(query, candidate));
                }

                if (capped) break;
            }

            TitleChoicesView.Refresh();
            UpdateResultCount();
            StatusTextBlock.Text = capped
                ? $"Title search stopped at the fixed {MaxTitleResultRows}-row cap. Tick any rows you want, or narrow the input and search again."
                : $"Title search complete. {TitleChoices.Count} raw TV result(s) retained. Tick any series you want.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Title search cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Title search stopped.";
            MessageBox.Show(this, ex.Message, "SubDL Bulk Download", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndOperation();
        }
    }

    private void TitleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateResultCount();
        RunBulkButton.IsEnabled = TitleChoices.Any(choice => choice.IsSelected);
    }

    private async void RunBulkButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedChoices = TitleChoices
            .Where(choice => choice.IsSelected)
            .GroupBy(choice => choice.SubdlId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (selectedChoices.Length == 0)
        {
            ShowInfo("Tick one or more SubDL series rows first.", "Choose series");
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            ShowInfo("Choose an output folder first.", "Bulk destination");
            return;
        }

        if (!TryGetSettings(out var settings, out var savingNewKey)) return;

        BeginOperation("Checking full-load search quota…");
        ProgressBar.Maximum = selectedChoices.Length * SubdlProClient.SeasonSearchLimit;
        ProgressBar.Value = 0;

        try
        {
            Directory.CreateDirectory(outputFolder);
            await using var client = new SubdlProClient(settings);
            await client.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);

            var before = await client.GetAccountUsageAsync(_operationCts.Token);
            var plannedSeasonSearchCalls = selectedChoices.Length * SubdlProClient.SeasonSearchLimit;
            if (before.SearchRemaining < plannedSeasonSearchCalls)
                throw new InvalidOperationException($"Full-load preflight failed: need {plannedSeasonSearchCalls} season-search request(s), but SubDL reports only {before.SearchRemaining} remaining. Nothing was scanned or downloaded.");

            var results = new List<BulkSeriesResult>();
            var completedSeasonSearchCalls = 0;

            foreach (var choice in selectedChoices)
            {
                _operationCts.Token.ThrowIfCancellationRequested();
                var title = choice.Candidate;
                var baseCalls = completedSeasonSearchCalls;
                var progress = new Progress<RawSeasonSearchProgress>(update =>
                {
                    ProgressBar.Maximum = selectedChoices.Length * SubdlProClient.SeasonSearchLimit;
                    ProgressBar.Value = Math.Min(ProgressBar.Maximum, baseCalls + update.SeasonsCompleted);
                    StatusTextBlock.Text = $"{title.Name}: internal shovel pass S{update.SeasonNumber:00}/{update.TotalSeasons:00}";
                });

                var rows = await client.SearchRawSeasonResultsAsync(title, progress, _operationCts.Token);
                completedSeasonSearchCalls += SubdlProClient.SeasonSearchLimit;
                ProgressBar.Value = completedSeasonSearchCalls;
                results.Add(new BulkSeriesResult(choice.Query, title, rows));
            }

            var diagnosticRows = results.SelectMany(result => result.Rows).Where(row => !row.IsRawRow).ToArray();
            var beforeDownload = await client.GetAccountUsageAsync(_operationCts.Token);
            var downloadRows = results
                .SelectMany(result => result.Rows
                    .Where(row => row.IsRawRow && row.HasDownloadUrl)
                    .Select(row => new BulkDownloadItem(result, row)))
                .ToArray();

            var manifestPath = Path.Combine(outputFolder, $"subdl-bulk-manifest-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await WriteManifestAsync(
                manifestPath,
                selectedChoices,
                results,
                before,
                beforeDownload,
                null,
                _lastTitleSearchCalls,
                completedSeasonSearchCalls,
                0,
                0,
                _operationCts.Token);

            if (diagnosticRows.Length > 0)
                throw new InvalidOperationException($"Metadata scan returned {diagnosticRows.Length} diagnostic/error row(s). Full-load rule stops before download. Manifest: {manifestPath}");

            if (beforeDownload.DownloadsRemaining < downloadRows.Length)
                throw new InvalidOperationException($"Full-load preflight failed: need {downloadRows.Length} downloads, but SubDL reports only {beforeDownload.DownloadsRemaining} remaining. Metadata is saved; no ZIP downloads were started. Manifest: {manifestPath}");

            StatusTextBlock.Text = $"Bill approved: {downloadRows.Length} ZIP(s). Starting full load.";
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
                }

                ProgressBar.Value = attempted;
            }

            var after = await client.GetAccountUsageAsync(_operationCts.Token);
            await WriteManifestAsync(
                manifestPath,
                selectedChoices,
                results,
                before,
                beforeDownload,
                after,
                _lastTitleSearchCalls,
                completedSeasonSearchCalls,
                attempted,
                failed,
                _operationCts.Token,
                downloadRows);

            StatusTextBlock.Text = failed == 0
                ? $"Bulk load complete. {attempted} ZIP(s) saved. Search bill: {_lastTitleSearchCalls + completedSeasonSearchCalls}; download bill: {attempted}."
                : $"Bulk load complete with {failed} failed download(s). Saved {attempted - failed}/{attempted}. Manifest: {manifestPath}";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Bulk run cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Bulk run stopped.";
            MessageBox.Show(this, ex.Message, "SubDL Bulk Download", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            EndOperation();
        }
    }

    private static async Task WriteManifestAsync(
        string path,
        IReadOnlyList<BulkTitleChoice> selectedChoices,
        IReadOnlyList<BulkSeriesResult> results,
        SubdlAccountUsage before,
        SubdlAccountUsage beforeDownload,
        SubdlAccountUsage? after,
        int titleSearchCalls,
        int seasonSearchCalls,
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
            selected_series = selectedChoices.Select(choice => new
            {
                input = choice.Query,
                choice.Name,
                choice.Year,
                choice.SubdlId,
                choice.ImdbId
            }),
            bill = new
            {
                title_search_requests = titleSearchCalls,
                season_search_requests = seasonSearchCalls,
                total_search_requests = titleSearchCalls + seasonSearchCalls,
                download_candidates = results.SelectMany(result => result.Rows).Count(row => row.IsRawRow && row.HasDownloadUrl),
                download_attempts = downloadAttempts,
                download_failures = failedDownloads
            },
            quota_before_scan = before,
            quota_before_download = beforeDownload,
            quota_after = after,
            series = results.Select(result => new
            {
                input = result.Query,
                selected = new { result.Title.Name, result.Title.Year, result.Title.SubdlId, result.Title.ImdbId },
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

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private void BeginOperation(string status)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        SearchTitlesButton.IsEnabled = false;
        RunBulkButton.IsEnabled = false;
        BrowseOutputButton.IsEnabled = false;
        SeriesListTextBox.IsEnabled = false;
        TitleResultsGrid.IsEnabled = false;
        ResultsFilterTextBox.IsEnabled = false;
        OutputFolderTextBox.IsEnabled = false;
        ApiKeyTextBox.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = status;
    }

    private void EndOperation()
    {
        SearchTitlesButton.IsEnabled = true;
        RunBulkButton.IsEnabled = TitleChoices.Any(choice => choice.IsSelected);
        BrowseOutputButton.IsEnabled = true;
        SeriesListTextBox.IsEnabled = true;
        TitleResultsGrid.IsEnabled = true;
        ResultsFilterTextBox.IsEnabled = true;
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
