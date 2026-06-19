using Argus.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Argus.App.Services;

public sealed class ProjectActionReviewService : IProjectActionReviewService
{
    private readonly SemaphoreSlim dialogGate = new(1, 1);

    public async Task<ProjectActionReviewDecision> RequestReviewAsync(
        ProjectAction action,
        CancellationToken cancellationToken = default)
    {
        await dialogGate.WaitAsync(cancellationToken);
        try
        {
            if (App.Window is null || App.DispatcherQueue is null)
            {
                return Denied("the Argus window is not ready for review");
            }

            if (App.DispatcherQueue.HasThreadAccess)
            {
                return await ShowDialogAsync(action, cancellationToken);
            }

            var completion = new TaskCompletionSource<ProjectActionReviewDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!App.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await ShowDialogAsync(action, cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetResult(Denied("the review was cancelled"));
                    }
                    catch
                    {
                        completion.TrySetResult(
                            Denied("the review dialog could not be displayed"));
                    }
                }))
            {
                return Denied("the review dialog could not be scheduled");
            }

            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Denied("the review was cancelled");
        }
        finally
        {
            dialogGate.Release();
        }
    }

    private static async Task<ProjectActionReviewDecision> ShowDialogAsync(
        ProjectAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (App.Window.Content is not FrameworkElement root ||
            root.XamlRoot is null)
        {
            return Denied("the Argus window is not ready for review");
        }

        var content = new StackPanel { Spacing = 10 };
        if (action.IsProposal)
        {
            content.Children.Add(new TextBlock
            {
                Text = "AI PROPOSAL - review before Argus does anything.",
                FontFamily = new FontFamily("Cascadia Code"),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = action.Explanation,
            TextWrapping = TextWrapping.Wrap
        });

        TextBox? taskTitle = null;
        if (action.Command == ProjectActionCommand.CreateTask)
        {
            taskTitle = new TextBox
            {
                Header = "Task title",
                Text = action.IsProposal
                    ? action.Title
                    : $"Next step for {action.ProjectTitle}",
                MaxLength = 220
            };
            content.Children.Add(taskTitle);
            content.Children.Add(new TextBlock
            {
                Text = "This creates one local Task node and links it to the project.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (action.Command == ProjectActionCommand.ResolveBlocker)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Only the blocker relationship will be removed. Neither item will be completed, archived, or deleted.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = GetTitle(action),
            Content = content,
            PrimaryButtonText = GetPrimaryButtonText(action),
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        using var registration = cancellationToken.Register(() =>
            App.DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary ||
            cancellationToken.IsCancellationRequested)
        {
            return Denied("you cancelled the project action");
        }

        if (taskTitle is not null && string.IsNullOrWhiteSpace(taskTitle.Text))
        {
            return Denied("a task title is required");
        }

        return new ProjectActionReviewDecision(
            true,
            taskTitle?.Text.Trim());
    }

    private static string GetTitle(ProjectAction action) =>
        action.Command switch
        {
            ProjectActionCommand.CreateTask => "Review task creation",
            ProjectActionCommand.ResolveBlocker => "Remove blocker relationship?",
            _ when action.IsProposal => "Review AI proposal",
            _ => "Review project action"
        };

    private static string GetPrimaryButtonText(ProjectAction action) =>
        action.Command switch
        {
            ProjectActionCommand.CreateTask => "Create task",
            ProjectActionCommand.ResolveBlocker => "Remove link",
            _ => "Continue"
        };

    private static ProjectActionReviewDecision Denied(string reason) =>
        new(false, Reason: reason);
}
