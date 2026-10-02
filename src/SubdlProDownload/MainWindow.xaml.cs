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
    public string ReleaseLabel => $"Release {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0"}";

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

        BeginOperation("Scanning library…");
        try
        {
            var cancellationToken = _operationCts!.Token;
            var items = await Task.Run(() => _scanner.Scan(root, cancellationToken), cancellationToken);
            Videos.Clear();
            foreach (var item in items) Videos.Add(item);

            var missing = Videos.Count(x => !x.HasSubtitle);
            StatusTextBlock.Text = $"Scan complete. {Videos.Count} videos; {missing} missing English subtitles.";
            CountTextBlock.Text = $"{Videos.Count} videos";
            ProgressBar.Maximum = Math.Max(Videos.Count, 1);
            ProgressBar.Value = Videos.Count;
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Scan cancelled."; }
        catch (Exception ex) { ShowError("Scan failed", ex); }
        finally { EndOperation(); }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var missing = Videos.Where(x => !x.HasSubtitle).ToArray();
        if (missing.Length == 0)
        {
            ShowInfo("There are no missing subtitles in the current scan.", "SubDL Pro Download");
            return;
        }

        var enteredKey = ApiKeyTextBox.Text.Trim();
        var savedSettings = AppSettings.LoadSaved();
        var savingNewKey = !string.IsNullOrWhiteSpace(enteredKey);
        var settings = savingNewKey ? new AppSettings(enteredKey) : savedSettings;
        if (!settings.HasApiKey)
        {
            ShowInfo("Paste your SubDL Pro API key into the field first. After SubDL accepts it, this installation remembers it automatically.", "SubDL Pro API key");
            return;
        }

        BeginOperation($"Checking SubDL Pro credentials… 0/{missing.Length}");
        ProgressBar.Maximum = missing.Length;
        ProgressBar.Value = 0;
        var misses = new List<string>();

        try
        {
            await using var provider = new SubdlProClient(settings);
            await provider.InitializeAsync(_operationCts!.Token);

            // A mistyped replacement key must not overwrite a working saved key.
            if (savingNewKey)
            {
                settings.Save();
                ApiKeyTextBox.Clear();
            }

            var workflow = new SubtitleWorkflow(provider);
            for (var index = 0; index < missing.Length; index++)
            {
                var item = missing[index];
                _operationCts.Token.ThrowIfCancellationRequested();
                item.Status = "Searching SubDL…";
                StatusTextBlock.Text = $"Processing {index + 1}/{missing.Length}: {item.FileName}";

                try
                {
                    var result = await workflow.DownloadForVideoAsync(item.FullPath, "en", _operationCts.Token);
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
            StatusTextBlock.Text = $"Finished. {missing.Length - misses.Count} downloaded; {misses.Count} missing, ambiguous, or failed.";
        }
        catch (OperationCanceledException) { StatusTextBlock.Text = "Subtitle download cancelled."; }
        catch (Exception ex) { ShowError("SubDL Pro operation failed", ex); }
        finally { EndOperation(); }
    }

    private static void WriteMissReport(string libraryRoot, IReadOnlyCollection<string> misses)
    {
        var path = Path.Combine(libraryRoot, "SubDL-Misses.txt");
        if (misses.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var lines = new[]
        {
            "SubDL Pro Download - no subtitle downloaded",
            $"Generated: {DateTimeOffset.Now:O}",
            ""
        }.Concat(misses);
        File.WriteAllLines(path, lines);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private void BeginOperation(string status)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        BrowseButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        ApiKeyTextBox.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = status;
    }

    private void EndOperation()
    {
        BrowseButton.IsEnabled = true;
        ScanButton.IsEnabled = true;
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
