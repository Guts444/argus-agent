using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public class NewsService : INewsService
{
    private readonly HttpClient _http;
    private readonly string? _newsApiKey;

    private static readonly FeedSource[] RssFeeds =
    {
        new("The Verge", "https://www.theverge.com/rss/index.xml", "tech"),
        new("Ars Technica", "https://feeds.arstechnica.com/arstechnica/technology-lab", "tech"),
        new("Ars Gaming", "https://feeds.arstechnica.com/arstechnica/gaming", "gaming"),
        new("MIT AI", "https://news.mit.edu/rss/topic/artificial-intelligence2", "ai"),
        new("TechCrunch", "https://techcrunch.com/feed/", "tech"),
        new("OpenAI", "https://openai.com/news/rss.xml", "ai"),
        new("VentureBeat AI", "https://venturebeat.com/category/ai/feed/", "ai"),
        new("Google Research", "https://research.google/blog/rss/", "tech"),
        new("Qwen", "https://qwenlm.github.io/blog/index.xml", "ai"),
        new("Hugging Face", "https://huggingface.co/blog/feed.xml", "ai"),
        new("Polygon", "https://www.polygon.com/rss/index.xml", "gaming"),
        new("PC Gamer", "https://www.pcgamer.com/rss/", "gaming"),
        new("Game Developer", "https://www.gamedeveloper.com/rss.xml", "gaming"),
        new("Google DeepMind", "https://deepmind.google/blog/rss.xml", "ai"),
        new("Anthropic", "https://www.anthropic.com/index.xml", "ai"),
        new("SemiAnalysis", "https://www.semianalysis.com/feed", "tech"),
        new("Interconnects", "https://www.interconnects.ai/feed", "ai"),
        new("Kotaku", "https://kotaku.com/rss", "gaming"),
        new("Eurogamer", "https://www.eurogamer.net/feed/news", "gaming")
    };

    private static readonly string[] AiTerms =
    {
        " ai ", "artificial intelligence", "machine learning", "deep learning", "openai",
        "anthropic", "claude", "deepmind", "gemini", "google brain", "llm", "large language model",
        "neural", "agentic", "frontier model", "foundation model", "deepseek", "qwen", "alibaba",
        "moonshot", "kimi", "zhipu", "glm", "baidu", "ernie", "minimax", "tencent hunyuan",
        "chinese model", "china ai"
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public NewsService(HttpClient http)
    {
        _http = http;
        _newsApiKey = Environment.GetEnvironmentVariable("NEWSAPI_KEY");
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Argus/1.0");
        }
    }

    public async Task<IReadOnlyList<NewsArticle>> GetLatestAsync(
        int count = 20, string? category = null, CancellationToken ct = default)
    {
        var articles = new List<NewsArticle>();

        if (!string.IsNullOrEmpty(_newsApiKey))
        {
            try
            {
                var cat = string.Equals(category, "ai", StringComparison.OrdinalIgnoreCase)
                    ? "technology"
                    : category ?? "technology";
                var url = $"https://newsapi.org/v2/top-headlines?category={cat}&pageSize=20&apiKey={_newsApiKey}";
                var response = await _http.GetFromJsonAsync<NewsApiResponse>(url, JsonOpts, ct);
                if (response?.Articles is not null)
                {
                    articles.AddRange(response.Articles.Select(MapNewsApiArticle));
                }
            }
            catch
            {
                // RSS feeds below keep the dashboard useful without API credentials.
            }
        }

        var rssArticles = await FetchRssAsync(ct);
        var existingUrls = articles.Select(a => a.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        articles.AddRange(rssArticles.Where(a => !existingUrls.Contains(a.Url)));

        return articles
            .Where(article => MatchesCategory(article, category))
            .GroupBy(article => string.IsNullOrWhiteSpace(article.Url) ? article.Title : article.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(a => a.PublishedAt)
            .Take(count)
            .ToList();
    }

    private async Task<IReadOnlyList<NewsArticle>> FetchRssAsync(CancellationToken ct)
    {
        var articles = new List<NewsArticle>();
        foreach (var feed in RssFeeds)
        {
            try
            {
                var xml = await _http.GetStringAsync(feed.Url, ct);
                var doc = XDocument.Parse(xml);
                var feedArticles = doc.Root?.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase) == true
                    ? ParseAtomFeed(doc, feed)
                    : ParseRssFeed(doc, feed);
                articles.AddRange(feedArticles);
            }
            catch
            {
                // One failed publisher should not blank the whole dashboard.
            }
        }

        return articles;
    }

    private static IEnumerable<NewsArticle> ParseRssFeed(XDocument doc, FeedSource feed)
    {
        return doc
            .Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Take(12)
            .Select(item =>
            {
                var title = Value(item, "title");
                var link = Value(item, "link");
                var description = StripHtml(Value(item, "description"));
                var category = InferCategory(
                    feed.DefaultCategory,
                    title,
                    description,
                    string.Join(" ", Values(item, "category")));

                return new NewsArticle
                {
                    Title = WebUtility.HtmlDecode(title),
                    Summary = Truncate(description, 260),
                    Source = feed.Name,
                    Url = link,
                    ImageUrl = MediaUrl(item),
                    Category = category,
                    PublishedAt = ParseDate(Value(item, "pubDate"))
                };
            })
            .Where(article => !string.IsNullOrWhiteSpace(article.Title));
    }

    private static IEnumerable<NewsArticle> ParseAtomFeed(XDocument doc, FeedSource feed)
    {
        return doc
            .Descendants()
            .Where(element => element.Name.LocalName == "entry")
            .Take(12)
            .Select(entry =>
            {
                var title = Value(entry, "title");
                var summary = StripHtml(Value(entry, "summary"));
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = StripHtml(Value(entry, "content"));
                }

                var categories = entry
                    .Elements()
                    .Where(element => element.Name.LocalName == "category")
                    .Select(element => element.Attribute("term")?.Value ?? element.Value);
                var category = InferCategory(feed.DefaultCategory, title, summary, string.Join(" ", categories));

                return new NewsArticle
                {
                    Title = WebUtility.HtmlDecode(title),
                    Summary = Truncate(summary, 260),
                    Source = feed.Name,
                    Url = AtomLink(entry),
                    ImageUrl = MediaUrl(entry),
                    Category = category,
                    PublishedAt = ParseDate(Value(entry, "published"), Value(entry, "updated"))
                };
            })
            .Where(article => !string.IsNullOrWhiteSpace(article.Title));
    }

    private static bool MatchesCategory(NewsArticle article, string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || string.Equals(category, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(category, "ai", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(article.Category, "ai", StringComparison.OrdinalIgnoreCase) ||
                ContainsAiSignal($"{article.Title} {article.Summary} {article.Source}");
        }

        if (string.Equals(category, "gaming", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(article.Category, "gaming", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(category, "tech", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(article.Category, "tech", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(article.Category, "ai", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(article.Category, category, StringComparison.OrdinalIgnoreCase);
    }

    private static string InferCategory(string fallback, params string[] textParts)
    {
        var text = $" {string.Join(' ', textParts).ToLowerInvariant()} ";
        return ContainsAiSignal(text) ? "ai" : fallback;
    }

    private static bool ContainsAiSignal(string text)
    {
        var normalized = $" {text.ToLowerInvariant()} ";
        return AiTerms.Any(normalized.Contains);
    }

    private static string Value(XElement element, string localName)
    {
        return element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value.Trim() ?? string.Empty;
    }

    private static IEnumerable<string> Values(XElement element, string localName)
    {
        return element.Elements()
            .Where(child => child.Name.LocalName == localName)
            .Select(child => child.Value.Trim())
            .Where(value => value.Length > 0);
    }

    private static string AtomLink(XElement entry)
    {
        var link = entry.Elements()
            .Where(element => element.Name.LocalName == "link")
            .FirstOrDefault(element =>
                element.Attribute("rel") is null ||
                string.Equals(element.Attribute("rel")?.Value, "alternate", StringComparison.OrdinalIgnoreCase));
        return link?.Attribute("href")?.Value ?? link?.Value.Trim() ?? string.Empty;
    }

    private static string? MediaUrl(XElement element)
    {
        return element
            .Descendants()
            .FirstOrDefault(child =>
                child.Name.LocalName is "content" or "thumbnail" &&
                child.Name.NamespaceName.Contains("search.yahoo.com", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("url")
            ?.Value;
    }

    private static DateTime ParseDate(params string[] values)
    {
        foreach (var value in values)
        {
            if (DateTime.TryParse(value, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return DateTime.UtcNow;
    }

    private static string StripHtml(string value)
    {
        var noTags = Regex.Replace(value, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(Regex.Replace(noTags, "\\s+", " ").Trim());
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static NewsArticle MapNewsApiArticle(NewsApiArticle a) => new()
    {
        Title = a.Title ?? "Untitled",
        Summary = Truncate(a.Description ?? string.Empty, 260),
        Source = a.Source?.Name ?? "Unknown",
        Url = a.Url ?? string.Empty,
        ImageUrl = a.UrlToImage,
        Category = InferCategory("tech", a.Title ?? string.Empty, a.Description ?? string.Empty),
        PublishedAt = a.PublishedAt
    };

    private sealed record FeedSource(string Name, string Url, string DefaultCategory);
    private sealed record NewsApiResponse(List<NewsApiArticle> Articles);
    private sealed record NewsApiArticle(string? Title, string? Description, string? Url, string? UrlToImage, NewsApiSource? Source, DateTime PublishedAt);
    private sealed record NewsApiSource(string? Name);
}
