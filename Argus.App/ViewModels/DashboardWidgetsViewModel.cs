using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.App.ViewModels;

public partial class DashboardWidgetsViewModel : ObservableObject
{
    private readonly ISystemMonitorService _sysMonitor;
    private readonly IStockService _stockService;
    private readonly INewsService _newsService;
    private readonly ISportsService _sportsService;
    private readonly IFeedConfigService _feedConfig;

    public DashboardWidgetsViewModel(
        ISystemMonitorService sysMonitor,
        IStockService stockService,
        INewsService newsService,
        ISportsService sportsService,
        IFeedConfigService feedConfig)
    {
        _sysMonitor = sysMonitor;
        _stockService = stockService;
        _newsService = newsService;
        _sportsService = sportsService;
        _feedConfig = feedConfig;
    }

    // --- System Metrics ---
    [ObservableProperty] public partial double CpuPercent { get; set; }
    [ObservableProperty] public partial string CpuText { get; set; } = "--";
    [ObservableProperty] public partial double RamPercent { get; set; }
    [ObservableProperty] public partial string RamPercentText { get; set; } = "--";
    [ObservableProperty] public partial string RamText { get; set; } = "";
    [ObservableProperty] public partial string NetDownText { get; set; } = "";
    [ObservableProperty] public partial string NetUpText { get; set; } = "";
    [ObservableProperty] public partial string DiskText { get; set; } = "";
    [ObservableProperty] public partial double GpuPercent { get; set; }
    [ObservableProperty] public partial string GpuText { get; set; } = "--";
    [ObservableProperty] public partial int ProcessCount { get; set; }
    [ObservableProperty] public partial string ProcessText { get; set; } = "--";
    [ObservableProperty] public partial string UptimeText { get; set; } = "";
    [ObservableProperty] public partial string LastUpdatedText { get; set; } = "";
    [ObservableProperty] public partial string CpuColor { get; set; } = "#FF00F0FF";
    [ObservableProperty] public partial string RamColor { get; set; } = "#FF00FF88";

    // --- Stocks ---
    public ObservableCollection<StockQuote> StockQuotes { get; } = new();
    [ObservableProperty] public partial bool StocksLoading { get; set; }
    [ObservableProperty] public partial string StocksError { get; set; } = "";
    [ObservableProperty] public partial string StocksStatusText { get; set; } = "Market waiting.";

    // --- News ---
    public ObservableCollection<NewsArticle> NewsArticles { get; } = new();
    [ObservableProperty] public partial string NewsCategory { get; set; } = "all";
    [ObservableProperty] public partial bool NewsLoading { get; set; }
    [ObservableProperty] public partial string NewsStatusText { get; set; } = "Waiting for tech, AI, and gaming intel.";

    // --- Sports ---
    public ObservableCollection<SportScore> SportScores { get; } = new();
    [ObservableProperty] public partial bool SportsLoading { get; set; }
    [ObservableProperty] public partial string SportsLeague { get; set; } = "eng.1";
    [ObservableProperty] public partial string SportsStatusText { get; set; } = "Waiting for soccer data.";

    // --- Config ---
    public FeedConfig? Config { get; private set; }

    [ObservableProperty] public partial string StocksConfigInput { get; set; } = "";
    [ObservableProperty] public partial string SelectedSportsLeague { get; set; } = "eng.1 (English Premier League)";
    [ObservableProperty] public partial string SportsTeamsInput { get; set; } = "";

    private static readonly System.Collections.Generic.Dictionary<string, string> LeagueMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "eng.1", "eng.1 (English Premier League)" },
        { "esp.1", "esp.1 (Spanish La Liga)" },
        { "ita.1", "ita.1 (Italian Serie A)" },
        { "ger.1", "ger.1 (German Bundesliga)" },
        { "fra.1", "fra.1 (French Ligue 1)" },
        { "usa.1", "usa.1 (MLS)" },
        { "uefa.champions", "uefa.champions (Champions League)" }
    };

    private bool _loaded;
    private CancellationTokenSource? _monitoringCts;

    [RelayCommand]
    public async Task LoadAllAsync()
    {
        if (_loaded)
        {
            StartLiveMonitoring();
            return;
        }

        _loaded = true;
        Config = await _feedConfig.LoadConfigAsync();
        SportsLeague = Config.FavoriteLeague;

        StocksConfigInput = string.Join(", ", Config.StockSymbols);
        SportsTeamsInput = string.Join(", ", Config.FavoriteTeams);
        if (LeagueMap.TryGetValue(Config.FavoriteLeague, out var leagueName))
        {
            SelectedSportsLeague = leagueName;
        }
        else
        {
            SelectedSportsLeague = Config.FavoriteLeague;
        }

        await RefreshSystemAsync();
        StartLiveMonitoring();
        await Task.WhenAll(RefreshStocksAsync(), RefreshNewsAsync(), RefreshSportsAsync());
    }

    public void RefreshSystem()
    {
        var m = _sysMonitor.GetCurrentMetrics();
        _ = RunOnUiAsync(() => ApplySystemMetrics(m));
    }

    private async Task RefreshSystemAsync()
    {
        var m = _sysMonitor.GetCurrentMetrics();
        await RunOnUiAsync(() => ApplySystemMetrics(m));
    }

    private void ApplySystemMetrics(SystemMetrics m)
    {
        CpuPercent = m.CpuPercent;
        CpuText = $"{m.CpuPercent:0.0}%";
        RamPercent = m.RamPercent;
        RamPercentText = $"{m.RamPercent:0.0}%";
        RamText = $"{m.RamUsedGb:F1} / {m.RamTotalGb:F1} GB";
        NetDownText = $"↓ {m.NetworkDownMbps:F1} Mbps";
        NetUpText = $"↑ {m.NetworkUpMbps:F1} Mbps";
        DiskText = $"R {m.DiskReadMbps:F1} · W {m.DiskWriteMbps:F1} MB/s";
        GpuPercent = m.GpuPercent;
        GpuText = $"{m.GpuPercent:0.0}% GPU";
        ProcessCount = m.ProcessCount;
        ProcessText = $"{m.ProcessCount} processes";
        UptimeText = FormatUptime(m.Uptime);
        LastUpdatedText = $"Updated {DateTime.Now:HH:mm:ss}";
        CpuColor = m.CpuPercent > 90 ? "#FFFF3366" : m.CpuPercent > 70 ? "#FFFFAA00" : "#FF00F0FF";
        RamColor = m.RamPercent > 90 ? "#FFFF3366" : m.RamPercent > 75 ? "#FFFFAA00" : "#FF00FF88";
    }

    public void StartLiveMonitoring()
    {
        if (_monitoringCts is { IsCancellationRequested: false })
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _monitoringCts = cts;
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var metrics = _sysMonitor.GetCurrentMetrics();
                    await RunOnUiAsync(() => ApplySystemMetrics(metrics));
                    await Task.Delay(1500, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }, ct);
    }

    public void StopLiveMonitoring()
    {
        _monitoringCts?.Cancel();
        _monitoringCts = null;
    }

    [RelayCommand]
    public async Task RefreshStocksAsync()
    {
        if (Config is null) return;
        StocksLoading = true;
        StocksError = "";
        StocksStatusText = "Refreshing market data...";
        try
        {
            var quotes = await _stockService.GetQuotesAsync(Config.StockSymbols);
            await RunOnUiAsync(() => Replace(StockQuotes, quotes));
            StocksStatusText = quotes.Count == 0
                ? "No market quotes returned."
                : $"{quotes.Count} quotes · updated {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            StocksError = ex.Message;
            StocksStatusText = "Market feed degraded.";
        }
        finally { StocksLoading = false; }
    }

    [RelayCommand]
    public async Task RefreshNewsAsync()
    {
        if (Config is null) return;
        NewsLoading = true;
        NewsStatusText = "Scanning tech, AI, and gaming feeds...";
        try
        {
            var cat = NewsCategory == "all" ? null : NewsCategory;
            var articles = await _newsService.GetLatestAsync(36, cat);
            await RunOnUiAsync(() => Replace(NewsArticles, articles));
            NewsStatusText = articles.Count == 0
                ? "No intel articles returned."
                : $"{articles.Count} intel signals · {DateTime.Now:HH:mm}";
        }
        catch
        {
            NewsStatusText = "Intel feed degraded.";
        }
        finally { NewsLoading = false; }
    }

    [RelayCommand]
    public async Task RefreshSportsAsync()
    {
        if (Config is null) return;
        SportsLoading = true;
        SportsStatusText = "Syncing soccer scoreboard...";
        try
        {
            var scores = await _sportsService.GetScoresAsync(
                Config.FavoriteLeague, Config.FavoriteTeams);
            await RunOnUiAsync(() => Replace(SportScores, scores));
            SportsStatusText = scores.Count == 0
                ? "No soccer fixtures returned."
                : $"{scores.Count} fixtures · ESPN soccer · {DateTime.Now:HH:mm}";
        }
        catch
        {
            SportsStatusText = "Soccer feed degraded.";
        }
        finally { SportsLoading = false; }
    }

    [RelayCommand]
    public async Task SetNewsCategoryAsync(string category)
    {
        NewsCategory = category;
        await RefreshNewsAsync();
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
        return $"{(int)t.TotalMinutes}m {t.Seconds}s";
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        try
        {
            var dispatcher = App.DispatcherQueue;
            if (dispatcher.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource();
            if (!dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                        tcs.SetResult();
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }))
            {
                action();
                tcs.SetResult();
            }

            return tcs.Task;
        }
        catch
        {
            action();
            return Task.CompletedTask;
        }
    }

    [RelayCommand]
    public async Task SaveStocksConfigAsync()
    {
        if (Config is null) return;
        var symbols = StocksConfigInput
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .ToList();
        if (symbols.Count > 0)
        {
            Config.StockSymbols = symbols;
            await _feedConfig.SaveConfigAsync(Config);
            await RefreshStocksAsync();
        }
    }

    [RelayCommand]
    public async Task SaveSportsConfigAsync()
    {
        if (Config is null) return;
        var leagueCode = "eng.1";
        foreach (var kvp in LeagueMap)
        {
            if (string.Equals(SelectedSportsLeague, kvp.Value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SelectedSportsLeague, kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                leagueCode = kvp.Key;
                break;
            }
        }
        if (SelectedSportsLeague.Contains(" ("))
        {
            leagueCode = SelectedSportsLeague.Split(" (")[0].Trim();
        }
        else
        {
            leagueCode = SelectedSportsLeague.Trim();
        }
        var teams = SportsTeamsInput
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        Config.FavoriteLeague = leagueCode;
        Config.FavoriteTeams = teams;
        SportsLeague = leagueCode;
        await _feedConfig.SaveConfigAsync(Config);
        await RefreshSportsAsync();
    }
}
