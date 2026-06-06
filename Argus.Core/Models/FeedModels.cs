namespace Argus.Core.Models;

/// <summary>
/// Real-time system hardware metrics snapshot.
/// </summary>
public class SystemMetrics
{
    public double CpuPercent { get; set; }
    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double RamPercent => RamTotalGb > 0 ? (RamUsedGb / RamTotalGb) * 100 : 0;
    public double DiskReadMbps { get; set; }
    public double DiskWriteMbps { get; set; }
    public double NetworkDownMbps { get; set; }
    public double NetworkUpMbps { get; set; }
    public double GpuPercent { get; set; }
    public int ProcessCount { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single stock quote snapshot.
/// </summary>
public class StockQuote
{
    public string Symbol { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public decimal CurrentPrice { get; set; }
    public decimal Change { get; set; }
    public decimal ChangePercent { get; set; }
    public decimal HighDay { get; set; }
    public decimal LowDay { get; set; }
    public decimal OpenDay { get; set; }
    public decimal PreviousClose { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A news article from any feed source.
/// </summary>
public class NewsArticle
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string Category { get; set; } = "general";
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A sports match/event score.
/// </summary>
public class SportScore
{
    public string Id { get; set; } = string.Empty;
    public string League { get; set; } = string.Empty;
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public string Status { get; set; } = "SCHEDULED"; // LIVE, FINAL, SCHEDULED
    public string? Period { get; set; }
    public DateTime? StartTime { get; set; }
    public string? HomeLogoUrl { get; set; }
    public string? AwayLogoUrl { get; set; }
}

/// <summary>
/// User-configurable feed preferences.
/// </summary>
public class FeedConfig
{
    public List<string> StockSymbols { get; set; } = new() { "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA", "META", "TSLA", "AMD" };
    public List<string> NewsCategories { get; set; } = new() { "tech", "ai", "gaming" };
    public List<string> FavoriteTeams { get; set; } = new(); // team IDs from ESPN
    public string FavoriteLeague { get; set; } = "eng.1"; // Premier League
    public int NewsRefreshMinutes { get; set; } = 5;
    public int StocksRefreshMinutes { get; set; } = 1;
    public int SportsRefreshMinutes { get; set; } = 2;
}
