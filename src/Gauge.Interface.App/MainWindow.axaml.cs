using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Gauge.Interface.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void BrowseOutputDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select output folder"
        });

        if (folders.Count > 0)
        {
            viewModel.OutputDirectory = folders[0].Path.LocalPath;
        }
    }

    private async void FileAction_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || sender is not Control control)
        {
            return;
        }

        if (control.DataContext is not GaugeFileRowViewModel file)
        {
            return;
        }

        viewModel.SelectedFile = file;

        if (file.IsDownloaded)
        {
            if (viewModel.ShowGraphCommand.CanExecute(null))
            {
                viewModel.ShowGraphCommand.Execute(null);
            }

            return;
        }

        await viewModel.DownloadSelectedAsync().ConfigureAwait(true);
    }

    private void GraphZoomWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            GaugeTrend.SetZoomWindowMode(toggle.IsChecked == true);
        }
    }

    private void GraphZoomIn_Click(object? sender, RoutedEventArgs e)
    {
        GaugeTrend.ZoomIn();
    }

    private void GraphZoomOut_Click(object? sender, RoutedEventArgs e)
    {
        GaugeTrend.ZoomOut();
    }

    private void GraphFit_Click(object? sender, RoutedEventArgs e)
    {
        GaugeTrend.Fit();
    }

    private void BackToFiles_Click(object? sender, RoutedEventArgs e)
    {
        GraphZoomWindowButton.IsChecked = false;
        GaugeTrend.SetZoomWindowMode(false);
    }
}
