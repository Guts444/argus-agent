using Argus.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Argus.App.Services;

public sealed class ToolApprovalService : IToolApprovalService
{
    private readonly SemaphoreSlim dialogGate = new(1, 1);

    public async Task<ToolApprovalDecision> RequestApprovalAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        await dialogGate.WaitAsync(cancellationToken);
        try
        {
            if (App.Window is null || App.DispatcherQueue is null)
            {
                return new ToolApprovalDecision(false, "the Argus window is not ready for approval");
            }

            if (App.DispatcherQueue.HasThreadAccess)
            {
                return await ShowDialogAsync(request, cancellationToken);
            }

            var completion = new TaskCompletionSource<ToolApprovalDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!App.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        completion.TrySetResult(await ShowDialogAsync(request, cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    catch
                    {
                        completion.TrySetResult(new ToolApprovalDecision(
                            false,
                            "the approval dialog could not be displayed"));
                    }
                }))
            {
                return new ToolApprovalDecision(false, "the approval dialog could not be scheduled");
            }

            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            dialogGate.Release();
        }
    }

    private static async Task<ToolApprovalDecision> ShowDialogAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (App.Window.Content is not FrameworkElement root || root.XamlRoot is null)
        {
            return new ToolApprovalDecision(false, "the Argus window is not ready for approval");
        }

        var riskLabel = request.RiskLevel == ToolRiskLevel.Destructive
            ? "Destructive action"
            : "Local data change";
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = $"Approve {request.ToolName}?",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{riskLabel}: {request.Description}",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = FormatArguments(request.ArgumentsJson),
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        MaxHeight = 220
                    }
                }
            },
            PrimaryButtonText = request.RiskLevel == ToolRiskLevel.Destructive ? "Delete" : "Approve",
            CloseButtonText = "Deny",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            ? new ToolApprovalDecision(true)
            : new ToolApprovalDecision(false, "you denied the action");
    }

    private static string FormatArguments(string argumentsJson)
    {
        const int maxLength = 1600;
        var trimmed = argumentsJson.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength] + "...";
    }
}
