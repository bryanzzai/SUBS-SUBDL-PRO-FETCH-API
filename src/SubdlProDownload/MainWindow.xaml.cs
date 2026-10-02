using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
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
    public string ReleaseLabel => $"Release {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.5.0"}";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save season ZIP files", Multiselect = false };
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
            CountTextBlock.Text = "0 diagnostic rows";
            ProgressBar.Value = 0;
            StatusTextBlock.Text = TitleCandidates.Count == 0
                ? "No TV-series results found. Try a shorter title."
                : $"Found {TitleCandidates.Count} TV-series result(s). Choose the correct one, then run the raw S01-S15 scan.";
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
        BeginOperation($"Preparing raw S01-S15 scan for {title.Name}…");
        ProgressBar.Maximum = SubdlProClient.SeasonSearchLimit;
        ProgressBar.Value = 0;
        RawRows.Clear();

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
                    ? $"Requesting raw season {update.SeasonNumber}/{update.TotalSeasons}…"
                    : $"Season {update.SeasonNumber}/{update.TotalSeasons}: API returned {update.ApiRowsFound} row(s).";
            });

            var rows = await client.SearchRawSeasonResultsAsync(title, progress, _operationCts.Token);
            foreach (var row in rows) RawRows.Add(row);

            var summaryRows = RawRows.Count(row => row.Kind == "SUMMARY");
            var rawRows = RawRows.Count(row => row.Kind == "RAW");
            var diagnosticRows = RawRows.Count - rawRows;
            CountTextBlock.Text = $"{rawRows} raw rows + {diagnosticRows} diagnostics";
            ProgressBar.Maximum = SubdlProClient.SeasonSearchLimit;
            ProgressBar.Value = SubdlProClient.SeasonSearchLimit;
            StatusTextBlock.Text = $"Raw scan complete: {summaryRows}/15 seasons returned a subtitles[] array. Up to {SubdlProClient.RawRowsPerSeasonLimit} rows per season are shown without filtering.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Raw season scan cancelled."; }
        catch (Exception ex) { ShowError("Raw SubDL scan failed", ex); }
        finally { EndOperation(); }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SeasonPacks.Where(pack => pack.IsSelected && pack.IsDownloadable).ToArray();
        if (selected.Length == 0)
        {
            ShowInfo("Download is intentionally hidden in the 0.5.0 diagnostic build.", "Diagnostic build");
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder)) return;
        if (!TryGetSettings(out var settings, out var savingNewKey)) return;
        BeginOperation("Checking SubDL Pro credentials…");
        try
        {
            Directory.CreateDirectory(outputFolder);
            await using var client = new SubdlProClient(settings);
            await client.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);
            var titleName = (TitleResultsComboBox.SelectedItem as TitleCandidate)?.Name ?? "SubDL";
            foreach (var pack in selected)
            {
                var destination = Path.Combine(outputFolder, BuildArchiveName(titleName, pack));
                await client.DownloadSeasonPackAsync(pack, destination, _operationCts.Token);
            }
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "ZIP download cancelled."; }
        catch (Exception ex) { ShowError("ZIP download failed", ex); }
        finally { EndOperation(); }
    }

    private static string BuildArchiveName(string titleName, SeasonPackItem pack)
    {
        var name = $"{titleName} {pack.SeasonLabel} - {pack.SubtitleId}.zip";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
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
        DownloadButton.IsEnabled = false;
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
