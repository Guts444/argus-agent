using Argus.Core.Services;
using Argus.Data.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace Argus.App;

public sealed partial class StartupPage : Page
{
    private readonly DatabaseStartupService startupService;
    private readonly DatabaseBackupService backupService;
    private bool isRunning;
    private bool telegramStarted;

    public StartupPage()
    {
        startupService = App.Services.GetRequiredService<DatabaseStartupService>();
        backupService = App.Services.GetRequiredService<DatabaseBackupService>();
        InitializeComponent();
        Loaded += StartupPage_OnLoaded;
    }

    private async void StartupPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= StartupPage_OnLoaded;
        await RunStartupAsync();
    }

    private async Task RunStartupAsync()
    {
        if (isRunning)
        {
            return;
        }

        isRunning = true;
        RecoveryPanel.Visibility = Visibility.Collapsed;
        StartupProgressRing.IsActive = true;
        StartupStatusText.Text = "Preparing Argus";
        StartupDetailText.Text = "Checking local workspace data.";

        var progress = new Progress<DatabaseInitializationProgress>(update =>
        {
            StartupStatusText.Text = update.Status;
            StartupDetailText.Text = update.Detail;
        });

        try
        {
            var result = await startupService.StartAsync(progress);
            StartupStatusText.Text = "Workspace ready";
            StartupDetailText.Text = result.Backup.Created
                ? "Local data is protected and Argus is ready."
                : "Local data checks completed and Argus is ready.";
            StartupProgressRing.IsActive = false;

            if (App.Window is MainWindow mainWindow)
            {
                mainWindow.ShowMainPage();
            }

            if (!telegramStarted)
            {
                telegramStarted = true;
                _ = App.Services.GetRequiredService<ITelegramGatewayService>().StartAsync();
            }
        }
        catch (Exception ex)
        {
            StartupProgressRing.IsActive = false;
            StartupStatusText.Text = "Argus could not open the workspace";
            StartupDetailText.Text = ex.Message;
            ShowRecoveryOptions();
        }
        finally
        {
            isRunning = false;
        }
    }

    private void ShowRecoveryOptions()
    {
        var latestBackup = backupService.GetLatestBackupPath();
        RestoreButton.IsEnabled = !string.IsNullOrWhiteSpace(latestBackup);
        RecoveryDetailText.Text = string.IsNullOrWhiteSpace(latestBackup)
            ? "No startup backup is available yet. Retry startup or open the data folder to inspect the local database."
            : $"Latest verified backup: {Path.GetFileName(latestBackup)}";
        RecoveryPanel.Visibility = Visibility.Visible;
    }

    private async void RetryButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunStartupAsync();
    }

    private async void RestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (isRunning)
        {
            return;
        }

        isRunning = true;
        SetRecoveryButtonsEnabled(false);
        StartupProgressRing.IsActive = true;
        StartupStatusText.Text = "Restoring local workspace";
        StartupDetailText.Text = "Validating and restoring the latest startup backup.";

        try
        {
            var result = await backupService.RestoreLatestBackupAsync();
            StartupDetailText.Text = result.Message;
        }
        catch (Exception ex)
        {
            StartupProgressRing.IsActive = false;
            StartupStatusText.Text = "Backup restore failed";
            StartupDetailText.Text = ex.Message;
            ShowRecoveryOptions();
            return;
        }
        finally
        {
            isRunning = false;
            SetRecoveryButtonsEnabled(true);
        }

        await RunStartupAsync();
    }

    private async void OpenDataFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(backupService.DataDirectory);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            StartupStatusText.Text = "Could not open the data folder";
            StartupDetailText.Text = ex.Message;
        }
    }

    private void SetRecoveryButtonsEnabled(bool enabled)
    {
        RetryButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled && backupService.GetLatestBackupPath() is not null;
    }
}
