using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using TrueSize.Core.Model;
using TrueSize.Core.Presentation;
using TrueSize.Core.Remote;
using TrueSize.Core.Scanning;

namespace TrueSize.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueueTimer _progressTimer;
    private ScanHelperSession? _helper;
    private CancellationTokenSource? _cancellation;
    private ScanProgress? _progress;
    private Stopwatch? _elapsed;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _progressTimer = dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(200);
        _progressTimer.Tick += (_, _) => UpdateProgressText();
    }

    public RowCollection Rows { get; } = new();

    public static IReadOnlyList<string> HardlinkModes { get; } = ["Erster Fund", "Anteilig", "Separat ausweisen"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial string Path { get; set; } = string.Empty;

    /// <summary>Use the elevated helper (UAC prompt once per session) for the fast MFT scan on NTFS.</summary>
    [ObservableProperty]
    public partial bool UseElevatedScan { get; set; } = true;

    [ObservableProperty]
    public partial int HardlinkModeIndex { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "Ordner auswählen, Pfad eingeben oder einen Ordner aus dem Explorer hierher ziehen.";

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    [ObservableProperty]
    public partial bool IsInfoOpen { get; set; }

    [ObservableProperty]
    public partial string InfoTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity InfoSeverity { get; set; }

    [ObservableProperty]
    public partial TreeSortKey SortKey { get; set; } = TreeSortKey.Allocated;

    partial void OnHardlinkModeIndexChanged(int value)
    {
        // Re-aggregation only, no rescan.
        if (Rows.Model is { } model)
        {
            model.Tree.Aggregate((HardlinkMode)value);
            model.Sort(SortKey);
            Rows.Refresh();
            UpdateSummary(model.Tree, null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        string path = Path.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            ShowInfo(InfoBarSeverity.Error, "Ordner nicht gefunden", path);
            return;
        }

        path = System.IO.Path.GetFullPath(path);
        Path = path;
        IsInfoOpen = false;
        IsScanning = true;
        _cancellation = new CancellationTokenSource();
        _progress = new ScanProgress();
        _elapsed = Stopwatch.StartNew();
        StatusText = "Scan wird gestartet …";
        _progressTimer.Start();

        var options = new ScanOptions { HardlinkMode = (HardlinkMode)HardlinkModeIndex };
        bool elevate = UseElevatedScan;
        var token = _cancellation.Token;
        var progress = _progress;
        try
        {
            var (result, note) = await Task.Run(() => RunScan(path, options, elevate, progress, token));
            Rows.SetModel(new FlatTreeModel(result.Tree, SortKey));
            HasResult = true;
            UpdateSummary(result.Tree, result);
            ReportFindings(result, note);
        }
        catch (OperationCanceledException)
        {
            ShowInfo(InfoBarSeverity.Informational, "Scan abgebrochen", string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ScanHelperException
                                       or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            ShowInfo(InfoBarSeverity.Error, "Scan fehlgeschlagen", ex.Message);
        }
        finally
        {
            _progressTimer.Stop();
            StatusText = string.Empty;
            IsScanning = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private bool CanScan() => !IsScanning && !string.IsNullOrWhiteSpace(Path);

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>Runs on a worker thread. Returns the result and an optional note for the user.</summary>
    private (ScanResult Result, string? Note) RunScan(string path, ScanOptions options, bool elevate, ScanProgress progress, CancellationToken token)
    {
        var choice = ScannerSelector.Select(path);
        string? note = null;

        // The app never runs elevated itself; the fast MFT scan runs in the helper (NTFS only, ReFS has no fast path).
        if (elevate && choice.FastPathPossible && !choice.IsElevated)
        {
            try
            {
                _helper ??= ScanHelperSession.Start();
                return (_helper.Scan(path, options, progress, token), null);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                note = "Adminrechte wurden nicht erteilt. Es wurde ohne Adminrechte gescannt (langsamer, geschützte Ordner fehlen).";
            }
            catch (Exception ex) when (ex is IOException or ScanHelperException or InvalidDataException or UnauthorizedAccessException or TimeoutException or FileNotFoundException)
            {
                _helper?.Dispose();
                _helper = null;
                note = $"Der Scan-Helfer ist nicht verfügbar ({ex.Message}). Es wurde ohne Adminrechte gescannt.";
            }
        }

        try
        {
            return (choice.Scanner.Scan(path, options, progress, token), note);
        }
        catch (Exception ex) when (choice.IsFastPath && ex is IOException or InvalidDataException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return (new DirectoryScanner().Scan(path, options, progress, token), $"Schneller Scan nicht möglich ({ex.Message}).");
        }
    }

    public void ToggleRow(TreeRowViewModel row)
    {
        if (Rows.Model is { } model && row.CanExpand)
        {
            model.Toggle(row.RowIndex);
            Rows.Refresh();
        }
    }

    [RelayCommand]
    private void SortBy(string key)
    {
        if (!Enum.TryParse<TreeSortKey>(key, out var sortKey))
        {
            return;
        }

        SortKey = sortKey;
        if (Rows.Model is { } model)
        {
            model.Sort(sortKey);
            Rows.Refresh();
        }
    }

    public static void ShowInExplorer(TreeRowViewModel row) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", row.FullPath }, UseShellExecute = false });

    private void UpdateProgressText()
    {
        if (_progress is null || _elapsed is null)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        StatusText = string.Format(culture, "{0:N0} Dateien, {1:N0} Ordner, {2} · {3:F0} s",
            _progress.Files, _progress.Directories, SizeFormatter.Format(_progress.AllocatedBytes, culture), _elapsed.Elapsed.TotalSeconds);
    }

    private void UpdateSummary(ScanTree tree, ScanResult? result)
    {
        var culture = CultureInfo.CurrentCulture;
        string summary = string.Format(culture, "Belegt {0} · Größe {1} · Freigebbar {2} · {3:N0} Dateien · {4:N0} Ordner",
            SizeFormatter.Format(tree.GetTotalAllocated(ScanTree.Root), culture),
            SizeFormatter.Format(tree.GetTotalSize(ScanTree.Root), culture),
            SizeFormatter.Format(tree.GetFreeable(ScanTree.Root), culture),
            tree.GetFileCount(ScanTree.Root),
            tree.GetDirectoryCount(ScanTree.Root));
        if (result is not null)
        {
            summary += string.Format(culture, " · {0}, {1} · {2:F1} s", ScannerDisplayName(tree.Info.Scanner), tree.Info.FileSystem, result.Duration.TotalSeconds);
        }

        SummaryText = summary;
    }

    private static string ScannerDisplayName(string scanner) => scanner switch
    {
        "NtfsMft" => "MFT-Scan (Admin)",
        "Directory" => "Verzeichnis-Scan",
        _ => scanner,
    };

    private void ReportFindings(ScanResult result, string? note)
    {
        var messages = new List<string>();
        var severity = InfoBarSeverity.Informational;
        if (note is not null)
        {
            messages.Add(note);
            severity = InfoBarSeverity.Warning;
        }

        if (result.Errors.Count > 0)
        {
            messages.Add(string.Format(CultureInfo.CurrentCulture,
                "{0:N0} Elemente konnten nicht gelesen werden und fehlen in den Summen (z. B. {1}).", result.Errors.Count, result.Errors[0].Path));
            severity = InfoBarSeverity.Warning;
        }

        if (result.Tree.MinimumSharedAllocation is > 0 and var shared)
        {
            messages.Add($"Mindestens {SizeFormatter.Format(shared, CultureInfo.CurrentCulture)} sind zwischen Dateien geteilt (ReFS Block Cloning) und in „Belegt“ mehrfach gezählt.");
        }

        if (!result.Tree.Info.LinkCountsExact)
        {
            messages.Add("Hardlink-Anzahl unvollständig: „Freigebbar“ kann zu hoch sein.");
        }

        if (messages.Count > 0)
        {
            ShowInfo(severity, severity == InfoBarSeverity.Warning ? "Hinweise zum Ergebnis" : "Hinweis", string.Join(Environment.NewLine, messages));
        }
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        InfoSeverity = severity;
        InfoTitle = title;
        InfoMessage = message;
        IsInfoOpen = true;
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _helper?.Dispose();
        _helper = null;
    }
}
