using Argus.Core.Models;
using Argus.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Argus.App.Services;

public sealed record ProjectInstructionEditResult(
    bool Accepted,
    string Content);

public sealed class ProjectInstructionEditorService
{
    private readonly SemaphoreSlim dialogGate = new(1, 1);

    public async Task<ProjectInstructionEditResult> RequestEditAsync(
        Node project,
        string currentContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        await dialogGate.WaitAsync(cancellationToken);
        try
        {
            if (App.Window is null || App.DispatcherQueue is null)
            {
                return new(false, currentContent);
            }

            if (App.DispatcherQueue.HasThreadAccess)
            {
                return await ShowDialogAsync(
                    project,
                    currentContent,
                    cancellationToken);
            }

            var completion =
                new TaskCompletionSource<ProjectInstructionEditResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            if (!App.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        completion.TrySetResult(
                            await ShowDialogAsync(
                                project,
                                currentContent,
                                cancellationToken));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetResult(new(false, currentContent));
                    }
                    catch
                    {
                        completion.TrySetResult(new(false, currentContent));
                    }
                }))
            {
                return new(false, currentContent);
            }

            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(false, currentContent);
        }
        finally
        {
            dialogGate.Release();
        }
    }

    private static async Task<ProjectInstructionEditResult> ShowDialogAsync(
        Node project,
        string currentContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (App.Window.Content is not FrameworkElement root ||
            root.XamlRoot is null)
        {
            return new(false, currentContent);
        }

        var editor = new TextBox
        {
            Header = "Project instructions",
            Text = currentContent,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 180,
            MaxLength = ProjectInstructionPolicy.MaxLength,
            PlaceholderText =
                "Describe priorities, conventions, constraints, and preferred next steps."
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(editor);
        content.Children.Add(new TextBlock
        {
            Text =
                "Stored only in the local Argus database. When this project is active, a redacted version may be sent to the selected AI provider. Do not include credentials or private source content.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text =
                "Instructions shape priorities and conventions. They cannot change tool permissions, approval requirements, or proposal command limits.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = $"Instructions for {project.Title}",
            Content = content,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        using var registration = cancellationToken.Register(() =>
            App.DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary when !cancellationToken.IsCancellationRequested =>
                new(true, editor.Text),
            ContentDialogResult.Secondary when !cancellationToken.IsCancellationRequested =>
                new(true, string.Empty),
            _ => new(false, currentContent)
        };
    }
}
