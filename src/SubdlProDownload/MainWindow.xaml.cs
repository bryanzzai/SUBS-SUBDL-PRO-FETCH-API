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
    private readonly LibraryScanner _scanner = new();
    private CancellationTokenSource? _operationCts;

    public ObservableCollection<VideoItem> Videos { get; } = [];
    public ObservableCollection<TitleMapping> TitleMappings { get; } = [];
    public string ReleaseLabel => $"Release {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.2.0"}";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose video library", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(LibraryPathTextBox.Text) && Directory.Exists(LibraryPathTextBox.Text))
            dialog.InitialDirectory = LibraryPathTextBox.Text;
        if (dialog.ShowDialog(this) == true)
            LibraryPathTextBox.Text = dialog.FolderName;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var root = LibraryPathTextBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            ShowInfo("Choose a valid library folder first.", "SubDL Pro Download");
            return;
        }

        BeginOperation("Scanning library and reading titles / episodes…");
        try
        {
            var items = await Task.Run(() => _scanner.Scan(root, _operationCts!.Token), _operationCts!.Token);
            Videos.Clear();
            foreach (var item in items) Videos.Add(item);
            BuildTitleMappings(Videos.Where(video => !video.HasSubtitle));

            var missing = Videos.Count(video => !video.HasSubtitle);
            StatusTextBlock.Text = $"Scan complete. {Videos.Count} videos; {missing} missing English subtitles. Find title matches next.";
            CountTextBlock.Text = $"{Videos.Count} videos";
            ProgressBar.Maximum = Math.Max(Videos.Count, 1);
            ProgressBar.Value = Videos.Count;
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Scan cancelled."; }
        catch (Exception ex) { ShowError("Scan failed", ex); }
        finally { EndOperation(); }
    }

    private async void FindTitlesButton_Click(object sender, RoutedEventArgs e)
    {
        if (TitleMappings.Count == 0)
        {
            ShowInfo("Scan a library with missing subtitles first.", "Find SubDL titles");
            return;
        }

        if (!TryGetSettings(out var settings, out var savingNewKey)) return;
        BeginOperation($"Checking SubDL Pro credentials… 0/{TitleMappings.Count}");
        ProgressBar.Maximum = TitleMappings.Count;
        ProgressBar.Value = 0;

        try
        {
            await using var provider = new SubdlProClient(settings);
            await provider.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);

            for (var index = 0; index < TitleMappings.Count; index++)
            {
                var mapping = TitleMappings[index];
                mapping.Status = "Searching SubDL…";
                StatusTextBlock.Text = $"Finding title {index + 1}/{TitleMappings.Count}: {mapping.SuggestedTitle}";

                var candidates = await provider.SearchTitlesAsync(mapping.SuggestedTitle, _operationCts.Token);
                mapping.Candidates.Clear();
                foreach (var candidate in candidates) mapping.Candidates.Add(candidate);
                mapping.SelectedCandidate = null;
                mapping.Status = candidates.Count == 0 ? "No candidates" : $"Choose one of {candidates.Count}";
                ProgressBar.Value = index + 1;
            }

            StatusTextBlock.Text = "Choose the correct SubDL title for each row, then download subtitles.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Title search cancelled."; }
        catch (Exception ex) { ShowError("SubDL title search failed", ex); }
        finally { EndOperation(); }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var missing = Videos.Where(video => !video.HasSubtitle).ToArray();
        if (missing.Length == 0)
        {
            ShowInfo("There are no missing subtitles in the current scan.", "SubDL Pro Download");
            return;
        }

        if (TitleMappings.Count == 0)
        {
            ShowInfo("Scan, then use Find title matches before downloading.", "SubDL Pro Download");
            return;
        }

        var unchosen = TitleMappings.Where(mapping => mapping.Candidates.Count > 0 && mapping.SelectedCandidate is null).ToArray();
        if (unchosen.Length > 0)
        {
            ShowInfo($"Choose a SubDL title for: {string.Join(", ", unchosen.Select(mapping => mapping.SuggestedTitle))}.", "Choose titles");
            return;
        }

        if (!TryGetSettings(out var settings, out var savingNewKey)) return;
        var titlesByGroup = TitleMappings.Where(mapping => mapping.SelectedCandidate is not null)
            .ToDictionary(mapping => mapping.GroupKey, mapping => mapping.SelectedCandidate!);

        BeginOperation($"Checking SubDL Pro credentials… 0/{missing.Length}");
        ProgressBar.Maximum = missing.Length;
        ProgressBar.Value = 0;
        var misses = new List<string>();

        try
        {
            await using var provider = new SubdlProClient(settings);
            await provider.InitializeAsync(_operationCts!.Token);
            SaveVerifiedKeyIfNeeded(settings, savingNewKey);
            var workflow = new SubtitleWorkflow(provider);

            for (var index = 0; index < missing.Length; index++)
            {
                var item = missing[index];
                _operationCts.Token.ThrowIfCancellationRequested();
                titlesByGroup.TryGetValue(item.Identity.GroupKey, out var selectedTitle);
                item.Status = selectedTitle is null ? "Searching release…" : $"Searching {selectedTitle.Name}…";
                StatusTextBlock.Text = $"Processing {index + 1}/{missing.Length}: {item.FileName}";

                try
                {
                    var result = await workflow.DownloadForVideoAsync(item, selectedTitle, "en", _operationCts.Token);
                    item.Status = result.Message;
                    if (result.Success) item.SubtitlePath = result.SubtitlePath;
                    else misses.Add(item.FullPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.Status = "Error: " + ex.Message;
                    misses.Add(item.FullPath);
                }
                ProgressBar.Value = index + 1;
            }

            WriteMissReport(LibraryPathTextBox.Text.Trim(), misses);
            StatusTextBlock.Text = $"Finished. {missing.Length - misses.Count} downloaded; {misses.Count} missing or failed.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Subtitle download cancelled."; }
        catch (Exception ex) { ShowError("SubDL Pro operation failed", ex); }
        finally { EndOperation(); }
    }

    private void BuildTitleMappings(IEnumerable<VideoItem> videos)
    {
        TitleMappings.Clear();
        foreach (var group in videos.GroupBy(video => video.Identity.GroupKey).OrderBy(group => group.First().Identity.Title))
        {
            var episodes = group.Where(video => video.Identity.IsEpisode).Select(video => video.Identity.EpisodeDisplay).Distinct().Order().ToArray();
            TitleMappings.Add(new TitleMapping
            {
                GroupKey = group.Key,
                SuggestedTitle = group.First().Identity.Title,
                Episodes = episodes.Length == 0
                    ? $"{group.Count()} video(s)"
                    : $"{group.Count()} video(s): {string.Join(", ", episodes)}"
            });
        }
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

    private static void WriteMissReport(string libraryRoot, IReadOnlyCollection<string> misses)
    {
        var path = Path.Combine(libraryRoot, "SubDL-Misses.txt");
        if (misses.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var lines = new[] { "SubDL Pro Download - no subtitle downloaded", $"Generated: {DateTimeOffset.Now:O}", "" }.Concat(misses);
        File.WriteAllLines(path, lines);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private void BeginOperation(string status)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        BrowseButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        FindTitlesButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        ApiKeyTextBox.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = status;
    }

    private void EndOperation()
    {
        BrowseButton.IsEnabled = true;
        ScanButton.IsEnabled = true;
        FindTitlesButton.IsEnabled = true;
        DownloadButton.IsEnabled = true;
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
