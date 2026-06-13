using Argus.AI.Services;
using Argus.Core.Models;
using Argus.Core.Services;
using Argus.Data.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Argus.App;

public sealed partial class StartupPage : Page
{
    private readonly DatabaseStartupService startupService;
    private readonly DatabaseBackupService backupService;
    private readonly ISettingsService settingsService;
    private readonly ISecretStore secretStore;
    private readonly IAiProviderRegistry aiProviderRegistry;
    private readonly IOpenAiCodexService? openAiCodexService;
    private bool isRunning;
    private bool telegramStarted;

    private readonly List<ProviderDisplayItem> providerDisplayItems = new();
    private ProviderDisplayItem? selectedProviderItem;
    private int wizardStep;
    private bool isFirstRun;
    private bool codexLoginInProgress;

    public StartupPage()
    {
        startupService = App.Services.GetRequiredService<DatabaseStartupService>();
        backupService = App.Services.GetRequiredService<DatabaseBackupService>();
        settingsService = App.Services.GetRequiredService<ISettingsService>();
        secretStore = App.Services.GetRequiredService<ISecretStore>();
        aiProviderRegistry = App.Services.GetRequiredService<IAiProviderRegistry>();
        openAiCodexService = App.Services.GetService<IOpenAiCodexService>();
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
        WizardSection.Visibility = Visibility.Collapsed;
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

            // Check if this is a first run (no SetupCompleted marker)
            var setupCompleted = await settingsService.GetSettingAsync("SetupCompleted");
            isFirstRun = string.IsNullOrWhiteSpace(setupCompleted);

            if (isFirstRun)
            {
                // Collapse DB progress and show wizard
                DbProgressArea.Visibility = Visibility.Collapsed;
                DbProgressSection.Visibility = Visibility.Collapsed;
                StartupSubtitleText.Text = "FIRST-RUN SETUP";
                FooterText.Text = "You can change these settings later. Choose at least a provider to get started.";
                await InitializeWizardAsync();
            }
            else
            {
                await FinishStartupAsync();
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

    private async Task FinishStartupAsync()
    {
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

    // ─── WIZARD INITIALIZATION ──────────────────────────────────────────

    private async Task InitializeWizardAsync()
    {
        WizardSection.Visibility = Visibility.Visible;

        // Load provider profiles from DB and build display items
        var profiles = await settingsService.GetAiProviderProfilesAsync();
        providerDisplayItems.Clear();
        foreach (var profile in profiles)
        {
            var capabilities = aiProviderRegistry.GetCapabilities(profile);
            var description = GetProviderDescription(profile, capabilities);
            var authMode = capabilities.AuthenticationMode;
            providerDisplayItems.Add(new ProviderDisplayItem(
                profile, capabilities, description, authMode));
        }

        // Move Codex to the top (no API key needed, easiest setup)
        // Then sort: API-key providers, then local
        providerDisplayItems.Sort((a, b) =>
        {
            if (a.Profile.ProviderType == "OpenAICodex") return -1;
            if (b.Profile.ProviderType == "OpenAICodex") return 1;
            if (a.AuthenticationMode == AiAuthenticationMode.LocalOptional &&
                b.AuthenticationMode != AiAuthenticationMode.LocalOptional) return 1;
            if (b.AuthenticationMode == AiAuthenticationMode.LocalOptional &&
                a.AuthenticationMode != AiAuthenticationMode.LocalOptional) return -1;
            return string.CompareOrdinal(a.Profile.Name, b.Profile.Name);
        });

        ProviderList.ItemsSource = providerDisplayItems;
        ShowWizardStep(1);
    }

    // ─── WIZARD STEPS ───────────────────────────────────────────────────

    private void ShowWizardStep(int step)
    {
        wizardStep = step;
        WizardStep1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        WizardStep2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        WizardStep3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        WizardBackButton.Visibility = step > 1 ? Visibility.Visible : Visibility.Collapsed;
        WizardNextButton.Visibility = step < 3 ? Visibility.Visible : Visibility.Collapsed;
        WizardFinishButton.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        WizardSkipButton.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;

        WizardErrorText.Visibility = Visibility.Collapsed;

        switch (step)
        {
            case 1:
                WizardStepLabel.Text = "Step 1 of 3";
                WizardStepTitle.Text = "Connect an LLM provider";
                WizardStepDescription.Text = "Choose a provider and enter its credentials. You can change this later in Settings.";
                break;
            case 2:
                WizardStepLabel.Text = "Step 2 of 3";
                WizardStepTitle.Text = "Select a projects folder";
                WizardStepDescription.Text = "Choose a root directory for local projects. Argus scans it for README previews and Git state.";
                break;
            case 3:
                WizardStepLabel.Text = "Step 3 of 3";
                WizardStepTitle.Text = "Review privacy defaults";
                WizardStepDescription.Text = "These settings control what Argus shares with LLM providers and remote gateways.";
                UpdatePrivacySummary();
                break;
        }
    }

    // ─── STEP 1: PROVIDER SELECTION ─────────────────────────────────────

    private void ProviderList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is not ProviderDisplayItem item)
        {
            selectedProviderItem = null;
            HideCredentialEntry();
            return;
        }

        selectedProviderItem = item;
        ShowCredentialEntry(item);
    }

    private void ShowCredentialEntry(ProviderDisplayItem item)
    {
        ApiKeyEntry.Visibility = Visibility.Collapsed;
        CodexLoginSection.Visibility = Visibility.Collapsed;
        LocalNotice.Visibility = Visibility.Collapsed;

        switch (item.AuthenticationMode)
        {
            case AiAuthenticationMode.ApiKey:
                ApiKeyEntry.Visibility = Visibility.Visible;
                ApiKeyLabel.Text = $"{item.Profile.Name} API Key";
                ApiKeyBox.PlaceholderText = item.Profile.ProviderType switch
                {
                    "DeepSeek" => "sk-...",
                    "OpenAI" => "sk-proj-...",
                    "Anthropic" => "sk-ant-...",
                    "OpenRouter" => "sk-or-...",
                    _ => "Enter your API key"
                };
                ApiKeyHelpText.Text = item.Capabilities.AuthenticationHelpText;
                break;

            case AiAuthenticationMode.CodexAccount:
                CodexLoginSection.Visibility = Visibility.Visible;
                CodexStatusText.Text = "Sign in with your ChatGPT account. Codex stores its own tokens; Argus never copies them.";
                break;

            case AiAuthenticationMode.LocalOptional:
                LocalNotice.Visibility = Visibility.Visible;
                break;
        }
    }

    private void HideCredentialEntry()
    {
        ApiKeyEntry.Visibility = Visibility.Collapsed;
        CodexLoginSection.Visibility = Visibility.Collapsed;
        LocalNotice.Visibility = Visibility.Collapsed;
    }

    private async void CodexLoginButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (codexLoginInProgress || openAiCodexService is null)
        {
            return;
        }

        codexLoginInProgress = true;
        CodexLoginButton.IsEnabled = false;
        CodexStatusText.Text = "Checking Codex CLI...";

        try
        {
            var account = await openAiCodexService.GetAccountAsync();
            if (account.IsAuthenticated)
            {
                CodexStatusText.Text = $"Already signed in as {account.Email ?? "ChatGPT"}. {account.Status}";
                return;
            }

            CodexStatusText.Text = "Starting login flow...";
            var login = await openAiCodexService.StartLoginAsync();
            if (!login.Started)
            {
                CodexStatusText.Text = $"Login could not start: {login.Status}";
                return;
            }

            if (!string.IsNullOrWhiteSpace(login.AuthorizationUrl))
            {
                CodexStatusText.Text = $"Opening browser for ChatGPT sign-in. Complete the flow in your browser.";
                await Launcher.LaunchUriAsync(new Uri(login.AuthorizationUrl));
            }

            // Wait for completion with a 90-second timeout
            CodexStatusText.Text = "Waiting for sign-in to complete...";
            var completed = await openAiCodexService.CompleteLoginAsync(
                login.LoginId!,
                TimeSpan.FromSeconds(90));

            if (completed.IsAuthenticated)
            {
                CodexStatusText.Text = $"Signed in as {completed.Email ?? "ChatGPT"}. {completed.Status}";
                CodexLoginButton.Content = "✓ Signed in";
            }
            else
            {
                CodexStatusText.Text = $"Sign-in incomplete: {completed.Status}. You can retry or skip and configure later.";
                CodexLoginButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            CodexStatusText.Text = $"Sign-in failed: {ex.Message}. Make sure the Codex CLI is installed (npm install -g @openai/codex) and retry.";
            CodexLoginButton.IsEnabled = true;
        }
        finally
        {
            codexLoginInProgress = false;
        }
    }

    // ─── STEP 2: PROJECTS FOLDER ────────────────────────────────────────

    private async void BrowseFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");

        // WinUI 3 requires window handle for picker
        if (App.Window is not null)
        {
            InitializeWithWindow.Initialize(picker, App.WindowHandle);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ProjectsFolderBox.Text = folder.Path;
            ProjectsFolderHelpText.Text =
                $"Selected: {folder.Path} — {folder.Name} will be scanned for README previews and Git state.";
        }
    }

    // ─── STEP 3: PRIVACY REVIEW ─────────────────────────────────────────

    private void UpdatePrivacySummary()
    {
        var items = new List<string>();
        if (ProjectRedactionCheck.IsChecked == true)
            items.Add("• Project paths and credentials are redacted from LLM prompts");
        if (TelegramAllowlistCheck.IsChecked == true)
            items.Add("• Only allowed Telegram users can interact with Argus");
        if (AutoUpdateCheck.IsChecked == true)
            items.Add("• Argus checks for updates at startup with checksum verification");

        PrivacySummaryText.Text = items.Count == 0
            ? "All privacy protections are disabled. You can enable them later in Settings."
            : "With these settings:\n" + string.Join("\n", items);
    }

    // ─── WIZARD NAVIGATION ──────────────────────────────────────────────

    private void WizardBackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (wizardStep > 1)
        {
            ShowWizardStep(wizardStep - 1);
        }
    }

    private void WizardNextButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (wizardStep == 1)
        {
            // Validate: must select a provider (or we allow skip — they can configure later)
            // We're generous here: user can proceed without selecting
        }

        if (wizardStep < 3)
        {
            ShowWizardStep(wizardStep + 1);
        }
    }

    private void WizardSkipButton_OnClick(object sender, RoutedEventArgs e)
    {
        // Skip the entire wizard — just mark setup complete and go
        _ = CompleteSetupAsync(skipAll: true);
    }

    private async void WizardFinishButton_OnClick(object sender, RoutedEventArgs e)
    {
        await CompleteSetupAsync(skipAll: false);
    }

    // ─── SAVE AND FINISH ────────────────────────────────────────────────

    private async Task CompleteSetupAsync(bool skipAll)
    {
        WizardFinishButton.IsEnabled = false;
        WizardSkipButton.IsEnabled = false;
        WizardBackButton.IsEnabled = false;
        WizardNextButton.IsEnabled = false;
        WizardErrorText.Visibility = Visibility.Collapsed;

        try
        {
            if (!skipAll)
            {
                // Step 1: Save provider selection and credentials
                if (selectedProviderItem is not null)
                {
                    var profile = selectedProviderItem.Profile;
                    profile.IsDefault = true;

                    // Save API key if entered
                    if (!string.IsNullOrWhiteSpace(ApiKeyBox.Text) &&
                        selectedProviderItem.AuthenticationMode == AiAuthenticationMode.ApiKey &&
                        !string.IsNullOrWhiteSpace(profile.ApiKeyStorageKey))
                    {
                        await secretStore.SetSecretAsync(profile.ApiKeyStorageKey, ApiKeyBox.Text.Trim());
                    }

                    await settingsService.SaveAiProviderProfileAsync(profile);
                }

                // Step 2: Save projects folder if selected
                if (!string.IsNullOrWhiteSpace(ProjectsFolderBox.Text))
                {
                    await settingsService.SaveSettingAsync("ProjectsRootPath", ProjectsFolderBox.Text.Trim());
                }

                // Step 3: Save privacy defaults
                await settingsService.SaveSettingAsync(
                    "ProjectPrivacyRedaction",
                    ProjectRedactionCheck.IsChecked == true ? "true" : "false");
                await settingsService.SaveSettingAsync(
                    "TelegramAllowlistEnforced",
                    TelegramAllowlistCheck.IsChecked == true ? "true" : "false");
                await settingsService.SaveSettingAsync(
                    "AutoCheckForUpdates",
                    AutoUpdateCheck.IsChecked == true ? "true" : "false");
            }

            // Mark setup as complete
            await settingsService.SaveSettingAsync("SetupCompleted", DateTimeOffset.UtcNow.ToString("O"));

            await FinishStartupAsync();
        }
        catch (Exception ex)
        {
            WizardErrorText.Text = $"Could not save settings: {ex.Message}";
            WizardErrorText.Visibility = Visibility.Visible;
            WizardFinishButton.IsEnabled = true;
            WizardSkipButton.IsEnabled = true;
            WizardBackButton.IsEnabled = wizardStep > 1;
            WizardNextButton.IsEnabled = wizardStep < 3;
        }
    }

    // ─── PROVIDER DISPLAY HELPERS ───────────────────────────────────────

    private static string GetProviderDescription(AiProviderProfile profile, AiProviderCapabilities caps)
    {
        return caps.AuthenticationMode switch
        {
            AiAuthenticationMode.ApiKey => caps.Kind switch
            {
                AiProviderKind.DeepSeek => "API key · DeepSeek V4 models",
                AiProviderKind.OpenAi => "API key · GPT models",
                AiProviderKind.Anthropic => "API key · Claude models",
                AiProviderKind.OpenRouter => "API key · Multi-model routing",
                _ => "API key · OpenAI-compatible endpoint"
            },
            AiAuthenticationMode.CodexAccount => "ChatGPT account · No API key needed",
            AiAuthenticationMode.LocalOptional => "Local · No credentials required",
            _ => "API key required"
        };
    }

    // ─── RECOVERY ───────────────────────────────────────────────────────

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

/// <summary>
/// Display model for the provider list in the setup wizard.
/// </summary>
internal sealed class ProviderDisplayItem
{
    public AiProviderProfile Profile { get; }
    public AiProviderCapabilities Capabilities { get; }
    public string Description { get; }
    public AiAuthenticationMode AuthenticationMode { get; }

    public string Name => Profile.Name;

    public ProviderDisplayItem(
        AiProviderProfile profile,
        AiProviderCapabilities capabilities,
        string description,
        AiAuthenticationMode authenticationMode)
    {
        Profile = profile;
        Capabilities = capabilities;
        Description = description;
        AuthenticationMode = authenticationMode;
    }
}
