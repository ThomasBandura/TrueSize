using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;

namespace TrueSize.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(string? startPath)
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();

        // Multi-column table: name column plus 6 fixed columns (~650) and toolbar; derived window size in DIPs.
        double scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(780 * scale)));
        Closed += (_, _) => ViewModel.Dispose();

        if (!string.IsNullOrWhiteSpace(startPath))
        {
            ViewModel.Path = startPath;
            ViewModel.ScanCommand.Execute(null);
        }
    }

    public MainViewModel ViewModel { get; }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    // Width/height for the AppWindow are physical pixels.
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    private void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.ScanCommand.CanExecute(null))
        {
            ViewModel.ScanCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        var result = await picker.PickSingleFolderAsync();
        if (result is not null)
        {
            ViewModel.Path = result.Path;
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) && !ViewModel.IsScanning)
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Ordner scannen";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var folder = items.OfType<StorageFolder>().FirstOrDefault();
        string? path = folder?.Path ?? (items.FirstOrDefault() is StorageFile file ? Path.GetDirectoryName(file.Path) : null);
        if (path is not null && !ViewModel.IsScanning)
        {
            ViewModel.Path = path;
            ViewModel.ScanCommand.Execute(null);
        }
    }

    // ItemsRepeater does not set DataContext for x:Bind templates; the row template sets DataContext="{x:Bind}" explicitly.
    private static TreeRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as TreeRowViewModel;

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.ToggleRow(row);
        }
    }

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.ToggleRow(row);
        }
    }

    // Flyout items are not in the visual tree and do not inherit the row's DataContext: take it from the target.
    private TreeRowViewModel? _contextRow;

    private void RowMenu_Opening(object sender, object e) => _contextRow = RowOf((sender as FlyoutBase)?.Target!);

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_contextRow is { } row)
        {
            MainViewModel.ShowInExplorer(row);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_contextRow is { } row)
        {
            var package = new DataPackage();
            package.SetText(row.FullPath);
            Clipboard.SetContent(package);
        }
    }
}
