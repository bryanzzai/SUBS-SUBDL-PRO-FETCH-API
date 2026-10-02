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
    public string ReleaseLabel => $"Release {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.4.0"}";

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
            ShowInfo("Enter a series title first, for example Evil.", "Search SubDL");
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
            SeasonPacks.Clear();
            CountTextBlock.Text = "0 packages";
            ProgressBar.Value = 0;
            StatusTextBlock.Text = TitleCandidates.Count == 0
                ? "No TV-series results found. Try a shorter title."
                : $"Found {TitleCandidates.Count} TV-series result(s). Choose the correct one, then search its 15 season lists.";
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
        BeginOperation($"Preparing 15 English season searches for {title.Name}…");
        ProgressBar.Maximum = SubdlProClient.SeasonSearchLimit;
        ProgressBar.Value = 0;
        try
        {
            await using var client = new SubdlProClient(settings);
            await client.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);
            var progress = new Progress<SeasonSearchProgress>(update =>
            {
                ProgressBar.Maximum = update.TotalSeasons;
                ProgressBar.Value = update.SeasonsCompleted;
                StatusTextBlock.Text = update.MatchesFound is null
                    ? $"Searching season {update.SeasonNumber}/{update.TotalSeasons}: {update.Mask}"
                    : $"Season {update.SeasonNumber}/{update.TotalSeasons}: {update.MatchesFound} matching package(s).";
            });
            var packs = await client.SearchSeasonPacksAsync(title, progress, _operationCts.Token);
            SeasonPacks.Clear();
            foreach (var pack in packs) SeasonPacks.Add(pack);
            CountTextBlock.Text = $"{SeasonPacks.Count} packages";
            ProgressBar.Maximum = Math.Max(SeasonPacks.Count, 1);
            ProgressBar.Value = SeasonPacks.Count;
            StatusTextBlock.Text = SeasonPacks.Count == 0
                ? $"No package names matched {SubdlProClient.BuildSeasonMask(title.Name, 1)} through S15."
                : $"Found {SeasonPacks.Count} package(s) matching the title.sNN. masks. Tick the ZIP files you want to save.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Season-pack search cancelled."; }
        catch (Exception ex) { ShowError("Finding season packs failed", ex); }
        finally { EndOperation(); }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SeasonPacks.Where(pack => pack.IsSelected && pack.IsDownloadable).ToArray();
        if (selected.Length == 0)
        {
            ShowInfo("Tick one or more season packages first.", "Choose packages");
            return;
        }

        var outputFolder = OutputFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            ShowInfo("Choose a folder where the ZIP packages should be saved.", "Choose destination folder");
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
                var pack = selected[index];
                _operationCts.Token.ThrowIfCancellationRequested();
                pack.Status = "Downloading ZIP…";
                StatusTextBlock.Text = $"Downloading {index + 1}/{selected.Length}: {pack.SeasonLabel}";
                try
                {
                    var destination = Path.Combine(outputFolder, BuildArchiveName(titleName, pack));
                    await client.DownloadSeasonPackAsync(pack, destination, _operationCts.Token);
                    pack.Status = "Saved ZIP";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    pack.Status = "Failed: " + ex.Message;
                }
                ProgressBar.Value = index + 1;
            }
            StatusTextBlock.Text = failed == 0
                ? $"Finished. Saved {selected.Length} ZIP package(s)."
                : $"Finished. Saved {selected.Length - failed}; {failed} failed.";
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
        DownloadButton.IsEnabled = true;
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
