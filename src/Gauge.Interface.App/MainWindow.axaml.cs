using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Gauge.Interface.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void BrowseOutputDirectory_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
}
