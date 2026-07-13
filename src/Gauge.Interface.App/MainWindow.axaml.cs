using Avalonia.Controls;
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

    private void FileGraph_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || sender is not Control control)
        {
            return;
        }

        if (control.DataContext is GaugeFileRowViewModel file)
        {
            viewModel.SelectedFile = file;
        }

        if (viewModel.ShowGraphCommand.CanExecute(null))
        {
            viewModel.ShowGraphCommand.Execute(null);
        }
    }

    private async void DownloadSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.DownloadSelectedAsync().ConfigureAwait(true);
        }
    }
}
