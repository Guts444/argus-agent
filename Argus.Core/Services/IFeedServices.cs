using Argus.Core.Models;

namespace Argus.Core.Services;

public interface ISystemMonitorService
{
    SystemMetrics GetCurrentMetrics();
    IAsyncEnumerable<SystemMetrics> StreamMetricsAsync(TimeSpan interval, CancellationToken ct = default);
}

public interface IStockService
{
    Task<IReadOnlyList<StockQuote>> GetQuotesAsync(IEnumerable<string> symbols, CancellationToken ct = default);
    Task<StockQuote?> GetQuoteAsync(string symbol, CancellationToken ct = default);
}

public interface INewsService
{
    Task<IReadOnlyList<NewsArticle>> GetLatestAsync(int count = 20, string? category = null, CancellationToken ct = default);
}

public interface ISportsService
{
    Task<IReadOnlyList<SportScore>> GetScoresAsync(string league, IEnumerable<string>? teamIds = null, CancellationToken ct = default);
}

public interface IFeedConfigService
{
    Task<FeedConfig> LoadConfigAsync(CancellationToken ct = default);
    Task SaveConfigAsync(FeedConfig config, CancellationToken ct = default);
}
