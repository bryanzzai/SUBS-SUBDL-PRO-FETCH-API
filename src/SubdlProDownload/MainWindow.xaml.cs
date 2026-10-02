using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SubdlProDownload.Configuration;
using SubdlProDownload.Models;
using SubdlProDownload.Services;

namespace SubdlProDownload;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _operationCts;

    public ObservableCollection<TitleCandidate> TitleCandidates { get; } = [];
    public ObservableCollection<SeasonPackItem> SeasonPacks { get; } = [];
    public ObservableCollection<RawSubtitleRow> RawRows { get; } = [];
    public ICollectionView RawRowsView { get; }
    public string ReleaseLabel => $"Release {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.1"}";

    public MainWindow()
    {
        InitializeComponent();
        RawRowsView = CollectionViewSource.GetDefaultView(RawRows);
        RawRowsView.Filter = FilterRawRow;
        DataContext = this;
    }

    private bool FilterRawRow(object item)
    {
        if (item is not RawSubtitleRow row) return false;

        var filter = ResultsFilterTextBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(filter)) return true;

        return row.ReleaseName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void ResultsFilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RawRowsView?.Refresh();
        UpdateVisibleRowCount();
    }

    private void UpdateVisibleRowCount()
    {
        var totalRows = RawRows.Count(row => row.IsRawRow);
        var visibleRows = RawRowsView?.Cast<object>().Count(item => item is RawSubtitleRow row && row.IsRawRow) ?? totalRows;
        var diagnosticRows = RawRows.Count - totalRows;

        if (string.IsNullOrWhiteSpace(ResultsFilterTextBox?.Text))
        {
            CountTextBlock.Text = diagnosticRows == 0
                ? $"{totalRows} rows"
                : $"{totalRows} rows + {diagnosticRows} diagnostics";
        }
        else
        {
            CountTextBlock.Text = $"{visibleRows} of {totalRows} rows";
        }
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save subtitle ZIP files", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(OutputFolderTextBox.Text) && Directory.Exists(OutputFolderTextBox.Text))
            dialog.InitialDirectory = OutputFolderTextBox.Text;
        if (dialog.ShowDialog(this) == true)
            OutputFolderTextBox.Text = dialog.FolderName;
    }

    private async void SearchTitlesButton_Click(object sender, RoutedEventArgs e)
    {
        var query = TitleSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowInfo("Enter a series title first, for example Justified.", "Search SubDL");
            return;
        }

        if (!TryGetSettings(out var settings, out var savingNewKey)) return;
        BeginOperation("Checking SubDL Pro credentials…");
        try
        {
            await using var client = new SubdlProClient(settings);
            await client.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);
            StatusTextBlock.Text = $"Searching SubDL for {query}…";
            var candidates = await client.SearchTitlesAsync(query, _operationCts.Token);
            TitleCandidates.Clear();
            foreach (var candidate in candidates.Where(candidate => candidate.IsTvSeries)) TitleCandidates.Add(candidate);
            TitleResultsComboBox.SelectedIndex = -1;
            RawRows.Clear();
            SeasonPacks.Clear();
            RawRowsView.Refresh();
            UpdateVisibleRowCount();
            ProgressBar.Value = 0;
            StatusTextBlock.Text = TitleCandidates.Count == 0
                ? "No TV-series results found. Try a shorter title."
                : $"Found {TitleCandidates.Count} TV-series result(s). Choose the correct one, then run the S01-S15 scan.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Title search cancelled."; }
        catch (Exception ex) { ShowError("SubDL title search failed", ex); }
        finally { EndOperation(); }
    }

    private async void FindPacksButton_Click(object sender, RoutedEventArgs e)
    {
        if (TitleResultsComboBox.SelectedItem is not TitleCandidate title)
        {
            ShowInfo("Choose the actual TV series from the SubDL title list first.", "Choose a series");
            return;
        }

        if (!TryGetSettings(out var settings, out var savingNewKey)) return;
        BeginOperation($"Preparing S01-S15 scan for {title.Name}…");
        ProgressBar.Maximum = SubdlProClient.SeasonSearchLimit;
        ProgressBar.Value = 0;
        RawRows.Clear();
        RawRowsView.Refresh();

        try
        {
            await using var client = new SubdlProClient(settings);
            await client.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);

            var progress = new Progress<RawSeasonSearchProgress>(update =>
            {
                ProgressBar.Maximum = update.TotalSeasons;
                ProgressBar.Value = update.SeasonsCompleted;
                StatusTextBlock.Text = update.ApiRowsFound is null
                    ? $"Requesting season {update.SeasonNumber}/{update.TotalSeasons}…"
                    : $"Season {update.SeasonNumber}/{update.TotalSeasons}: API returned {update.ApiRowsFound} row(s).";
            });

            var rows = await client.SearchRawSeasonResultsAsync(title, progress, _operationCts.Token);
            foreach (var row in rows) RawRows.Add(row);
            RawRowsView.Refresh();
            UpdateVisibleRowCount();

            var rawRows = RawRows.Count(row => row.IsRawRow);
            ProgressBar.Maximum = SubdlProClient.SeasonSearchLimit;
            ProgressBar.Value = SubdlProClient.SeasonSearchLimit;
            StatusTextBlock.Text = $"Scan complete. {rawRows} subtitle row(s) retained; up to {SubdlProClient.RawRowsPerSeasonLimit} per season. Filter release_name or tick the packages you want to download.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Season scan cancelled."; }
        catch (Exception ex) { ShowError("SubDL scan failed", ex); }
        finally { EndOperation(); }
    }

    private void DownloadCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox checkBox || checkBox.DataContext is not RawSubtitleRow row)
            return;

        row.IsSelected = checkBox.IsChecked == true;
        DownloadButton.IsEnabled = RawRows.Any(candidate => candidate.IsSelected && candidate.IsRawRow);
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = RawRows.Where(row => row.IsSelected && row.IsRawRow).ToArray();
        if (selected.Length == 0)
        {
            ShowInfo("Tick one or more subtitle rows first.", "Choose subtitles");
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            ShowInfo("Choose a folder where the ZIP files should be saved.", "Choose destination folder");
            return;
        }

        if (!TryGetSettings(out var settings, out var savingNewKey)) return;
        BeginOperation($"Checking SubDL Pro credentials… 0/{selected.Length}");
        ProgressBar.Maximum = selected.Length;
        ProgressBar.Value = 0;
        var failed = 0;

        try
        {
            Directory.CreateDirectory(outputFolder);
            await using var client = new SubdlProClient(settings);
            await client.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);
            var titleName = (TitleResultsComboBox.SelectedItem as TitleCandidate)?.Name ?? "SubDL";

            for (var index = 0; index < selected.Length; index++)
            {
                var row = selected[index];
                _operationCts.Token.ThrowIfCancellationRequested();
                var identity = row.PackageId != "—" ? row.PackageId : $"season {row.SeasonValue}";
                StatusTextBlock.Text = $"Processing {index + 1}/{selected.Length}: {identity}";

                if (!row.HasDownloadUrl)
                {
                    failed++;
                    row.DownloadStatus = "Cannot download: API returned no download URL";
                    ProgressBar.Value = index + 1;
                    continue;
                }

                row.DownloadStatus = "Downloading ZIP…";
                try
                {
                    var destination = Path.Combine(outputFolder, BuildArchiveName(titleName, row));
                    await client.DownloadReturnedUrlAsync(row.DownloadUrl, destination, _operationCts.Token);
                    row.DownloadStatus = "Saved ZIP";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    row.DownloadStatus = "Failed: " + ex.Message;
                }

                ProgressBar.Value = index + 1;
            }

            StatusTextBlock.Text = failed == 0
                ? $"Finished. Saved {selected.Length} ZIP file(s)."
                : $"Finished. Saved {selected.Length - failed}; {failed} failed. See Download status for details.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "ZIP download cancelled."; }
        catch (Exception ex) { ShowError("ZIP download failed", ex); }
        finally { EndOperation(); }
    }

    private static string BuildArchiveName(string titleName, RawSubtitleRow row)
    {
        var release = row.ReleaseName != "—" ? row.ReleaseName : row.SourceName;
        var usefulRelease = string.IsNullOrWhiteSpace(release) || release == "—" ? "subtitle" : release;
        var identity = string.IsNullOrWhiteSpace(row.PackageId) || row.PackageId == "—" ? "no-package-id" : row.PackageId;
        var season = row.SeasonValue != "—" ? row.SeasonValue.PadLeft(2, '0') : row.QuerySeason.ToString("00");
        var name = $"{titleName} S{season} - {usefulRelease} - {identity}.zip";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return sanitized.Length <= 180 ? sanitized : sanitized[..176] + ".zip";
    }

    private bool TryGetSettings(out AppSettings settings, out bool savingNewKey)
    {
        var enteredKey = ApiKeyTextBox.Text.Trim();
        savingNewKey = !string.IsNullOrWhiteSpace(enteredKey);
        settings = savingNewKey ? new AppSettings(enteredKey) : AppSettings.LoadSaved();
        if (settings.HasApiKey) return true;
        ShowInfo("Paste your SubDL Pro API key into the field first. After SubDL accepts it, this installation remembers it automatically.", "SubDL Pro API key");
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
        FindPacksButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        BrowseOutputButton.IsEnabled = false;
        TitleSearchTextBox.IsEnabled = false;
        TitleResultsComboBox.IsEnabled = false;
        OutputFolderTextBox.IsEnabled = false;
        ApiKeyTextBox.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = status;
    }

    private void EndOperation()
    {
        SearchTitlesButton.IsEnabled = true;
        FindPacksButton.IsEnabled = true;
        DownloadButton.IsEnabled = RawRows.Any(row => row.IsSelected && row.IsRawRow);
        BrowseOutputButton.IsEnabled = true;
        TitleSearchTextBox.IsEnabled = true;
        TitleResultsComboBox.IsEnabled = true;
        OutputFolderTextBox.IsEnabled = true;
        ApiKeyTextBox.IsEnabled = true;
        CancelButton.IsEnabled = false;
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void ShowInfo(string message, string title) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowError(string title, Exception ex)
    {
        StatusTextBlock.Text = title + ".";
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
