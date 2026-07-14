using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Gauge.Core;

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

    private void SortByFileNumber_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SortFiles(FileListSortColumn.FileNumber);
        }
    }

    private void SortByFileSize_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SortFiles(FileListSortColumn.Size);
        }
    }

    private async void SaveRecord_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            sender is not Control { DataContext: GaugeFileRowViewModel file } ||
            file.Samples is not { Count: > 0 } samples)
        {
            return;
        }

        try
        {
            var startDirectory = string.IsNullOrWhiteSpace(viewModel.LastRecordExportDirectory)
                ? viewModel.OutputDirectory
                : viewModel.LastRecordExportDirectory;
            var startFolder = string.IsNullOrWhiteSpace(startDirectory)
                ? null
                : await StorageProvider.TryGetFolderFromPathAsync(startDirectory);

            var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save gauge record",
                SuggestedFileName = viewModel.BuildRecordFileName(file),
                SuggestedStartLocation = startFolder,
                DefaultExtension = "rec",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("Gauge record file")
                    {
                        Patterns = ["*.rec"]
                    }
                ]
            });

            if (destination is null)
            {
                return;
            }

            await using var stream = await destination.OpenWriteAsync();
            stream.SetLength(0);
            var metadata = viewModel.BuildLegacyRecordMetadata(file);
            await Task.Run(() => LegacyRecordExporter.Write(stream, metadata, samples));

            var savedPath = destination.Path.LocalPath;
            viewModel.RecordExportSucceeded(file, savedPath);
        }
        catch (Exception ex)
        {
            viewModel.RecordExportFailed(file, ex.Message);
        }
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
