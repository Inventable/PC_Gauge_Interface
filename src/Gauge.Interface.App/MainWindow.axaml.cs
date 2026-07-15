using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsSettingsOverlayVisible && viewModel.CloseSettingsOverlayCommand.CanExecute(null))
        {
            viewModel.CloseSettingsOverlayCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (viewModel.IsGraphVisible && viewModel.BackToFilesCommand.CanExecute(null))
        {
            GraphZoomWindowButton.IsChecked = false;
            GaugeTrend.SetZoomWindowMode(false);
            viewModel.BackToFilesCommand.Execute(null);
            e.Handled = true;
        }
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

    private void SerialSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.OpenSettingsCommand.CanExecute(null))
        {
            viewModel.OpenSettingsCommand.Execute(null);
        }
    }

    private void GaugeSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.OpenGaugeSettingsCommand.CanExecute(null))
        {
            viewModel.OpenGaugeSettingsCommand.Execute(null);
        }
    }

    private void EngineeringMode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && viewModel.OpenEngineeringModeCommand.CanExecute(null))
        {
            viewModel.OpenEngineeringModeCommand.Execute(null);
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

        if (file.HasPlotData)
        {
            if (viewModel.ShowGraphCommand.CanExecute(null))
            {
                GaugeTrend.ResetCursor();
                viewModel.ShowGraphCommand.Execute(null);
                GaugeTrend.Fit();
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

    private void CancelFileDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel &&
            sender is Control { DataContext: GaugeFileRowViewModel file })
        {
            viewModel.CancelDownload(file);
        }
    }

    private async void RetryFileDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel &&
            sender is Control { DataContext: GaugeFileRowViewModel file })
        {
            await viewModel.RetryDownloadAsync(file).ConfigureAwait(true);
        }
    }

    private void CancelSelectedDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { SelectedFile: not null } viewModel)
        {
            viewModel.CancelDownload(viewModel.SelectedFile);
        }
    }

    private async void RetrySelectedDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { SelectedFile: not null } viewModel)
        {
            await viewModel.RetryDownloadAsync(viewModel.SelectedFile).ConfigureAwait(true);
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

    private void GaugeTrend_CursorChanged(object? sender, ChartCursorEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.UpdateGraphCursor(e);
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
