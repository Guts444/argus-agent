using Microsoft.UI.Xaml;
using Argus.AI.Services;
using Argus.Core.Services;
using Argus.Data;
using Argus.Data.Services;
using Argus.App.Services;
using Argus.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Argus.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Services.GetRequiredService<ArgusDatabaseInitializer>().InitializeAsync().GetAwaiter().GetResult();

        _ = Services.GetRequiredService<ITelegramGatewayService>().StartAsync();

        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddArgusData(ArgusDataPaths.GetDefaultDatabasePath());
        services.AddSingleton<ISecretStore, WindowsSecretStore>();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IAiChatService, OpenAiCompatibleChatService>();
        services.AddSingleton<IToolService, ToolService>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<ITelegramGatewayService, TelegramGatewayService>();
        services.AddSingleton<ISoulService, SoulService>();
        services.AddSingleton<IProjectContextService, ProjectContextService>();
        services.AddSingleton<ISystemMonitorService, SystemMonitorService>();
        services.AddSingleton<IStockService, StockService>();
        services.AddSingleton<INewsService, NewsService>();
        services.AddSingleton<ISportsService, SportsService>();
        services.AddSingleton<IFeedConfigService, FeedConfigService>();
        services.AddSingleton<IAppUpdateService, GitHubUpdateService>();
        services.AddSingleton<MainPageViewModel>();
        services.AddSingleton<DashboardWidgetsViewModel>();
        return services.BuildServiceProvider();
    }
}
