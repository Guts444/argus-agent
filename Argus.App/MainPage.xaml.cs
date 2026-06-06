using Microsoft.UI.Xaml.Controls;
using Argus.App.ViewModels;
using Argus.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Argus.App;

/// <summary>
/// The main content page displayed inside the application window.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = App.Services.GetRequiredService<MainPageViewModel>();
        InitializeComponent();
        Loaded += MainPage_Loaded;

        ViewModel.ChatMessages.CollectionChanged += ChatMessages_CollectionChanged;

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedProvider))
            {
                ApiKeyPasswordBox.Password = string.Empty;
                ViewModel.ApiKeyInput = string.Empty;
            }
        };
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        await ViewModel.InitializeCommand.ExecuteAsync(null);
        GraphSurface.FitToView();
    }

    private void DashboardChatPulse_OnLoaded(object sender, RoutedEventArgs e)
    {
        DashboardChatPulse.Begin();
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            ViewModel.ApiKeyInput = passwordBox.Password;
        }
    }

    private void FitGraph_OnClick(object sender, RoutedEventArgs e)
    {
        GraphSurface.FitToView();
    }

    private void ResetGraph_OnClick(object sender, RoutedEventArgs e)
    {
        GraphSurface.ResetView();
    }

    private async void ClusterGraph_OnClick(object sender, RoutedEventArgs e)
    {
        await GraphSurface.ClusterByTypeAsync();
    }

    private async void MainPage_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var controlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (!controlDown)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.K:
                ViewModel.OpenCommandPaletteCommand.Execute(null);
                DispatcherQueue.TryEnqueue(() => CommandPaletteBox.Focus(FocusState.Keyboard));
                e.Handled = true;
                break;
            case VirtualKey.N:
                await ViewModel.NewNodeCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case VirtualKey.F:
                if (ViewModel.CurrentView == "Dashboard")
                {
                    ViewModel.ShowGraphCommand.Execute(null);
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    GlobalSearchBox.Focus(FocusState.Keyboard);
                    GlobalSearchBox.SelectAll();
                });
                e.Handled = true;
                break;
            case VirtualKey.G:
                ViewModel.ShowGraphCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.D:
                ViewModel.ShowDashboardCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                await ViewModel.SendChatCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
        }
    }

    private async void CommandPaletteBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await ViewModel.ExecuteCommandPaletteItemCommand.ExecuteAsync(ViewModel.SelectedCommandPaletteItem);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CloseCommandPaletteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down && CommandPaletteList.Items.Count > 0)
        {
            CommandPaletteList.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    private async void CommandPaletteList_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandPaletteItem item)
        {
            await ViewModel.ExecuteCommandPaletteItemCommand.ExecuteAsync(item);
        }
    }

    private async void ChatInputBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var shiftDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            if (!shiftDown)
            {
                e.Handled = true;
                await ViewModel.SendChatCommand.ExecuteAsync(null);
            }
        }
    }

    private void TelegramTokenBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            ViewModel.TelegramBotToken = passwordBox.Password;
        }
    }

    private async void GoToView1_OnClick(object sender, RoutedEventArgs e) => await RestoreViewAsync(1);
    private async void SaveView1_OnClick(object sender, RoutedEventArgs e) => await SaveViewAsync(1);
    private async void GoToView2_OnClick(object sender, RoutedEventArgs e) => await RestoreViewAsync(2);
    private async void SaveView2_OnClick(object sender, RoutedEventArgs e) => await SaveViewAsync(2);

    private async Task SaveViewAsync(int slot)
    {
        var zoom = GraphSurface.ZoomValue;
        var panX = GraphSurface.PanXValue;
        var panY = GraphSurface.PanYValue;
        var viewStr = $"{zoom:F4};{panX:F2};{panY:F2}";
        await ViewModel.SaveSettingAsync($"SavedView{slot}", viewStr);
        ViewModel.StatusText = $"Saved View {slot}.";
    }

    private async Task RestoreViewAsync(int slot)
    {
        var viewStr = await ViewModel.GetSettingAsync($"SavedView{slot}");
        if (string.IsNullOrWhiteSpace(viewStr))
        {
            ViewModel.StatusText = $"Saved View {slot} is empty.";
            return;
        }

        var parts = viewStr.Split(';');
        if (parts.Length == 3 &&
            double.TryParse(parts[0], out var zoom) &&
            double.TryParse(parts[1], out var panX) &&
            double.TryParse(parts[2], out var panY))
        {
            GraphSurface.ZoomValue = zoom;
            GraphSurface.PanXValue = panX;
            GraphSurface.PanYValue = panY;
            ViewModel.StatusText = $"Restored View {slot}.";
        }
        else
        {
            ViewModel.StatusText = $"Invalid saved view format.";
        }
    }

    private void ChatMessages_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel.ChatMessages.Count > 0)
            {
                if (DashboardChatList is not null)
                {
                    TryScrollToBottom(DashboardChatList, e);
                }
                if (BottomChatList is not null)
                {
                    TryScrollToBottom(BottomChatList, e);
                }
            }
        });
    }

    private void TryScrollToBottom(ListView listView, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (listView.Items.Count == 0) return;

        var sv = FindScrollViewer(listView);
        bool shouldScroll = false;

        if (sv is null)
        {
            shouldScroll = true;
        }
        else
        {
            bool isAtBottom = sv.VerticalOffset >= sv.ScrollableHeight - 40;
            bool isUserMsg = false;
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is Message m && m.Role == "user")
                    {
                        isUserMsg = true;
                        break;
                    }
                }
            }
            shouldScroll = isAtBottom || isUserMsg;
        }

        if (shouldScroll)
        {
            listView.ScrollIntoView(listView.Items[^1]);
        }
    }

    private ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer sv)
        {
            return sv;
        }
        int childrenCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            var result = FindScrollViewer(child);
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }
}
