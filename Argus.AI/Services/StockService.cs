using System.Text.Json;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

public class StockService : IStockService
{
    private readonly HttpClient _http;

    public StockService(HttpClient http)
    {
        _http = http;
    }

    public async Task<StockQuote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        var normalized = NormalizeSymbol(symbol);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(normalized)}?range=2d&interval=1d");
            request.Headers.UserAgent.ParseAdd("Argus/1.0");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var result = doc.RootElement
                .GetProperty("chart")
                .GetProperty("result")
                .EnumerateArray()
                .FirstOrDefault();

            if (result.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            return MapYahooChart(normalized, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<StockQuote>> GetQuotesAsync(
        IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var tasks = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => GetQuoteAsync(s, ct));
        var results = await Task.WhenAll(tasks);
        return results.Where(q => q is not null).ToList()!;
    }

    private static StockQuote? MapYahooChart(string symbol, JsonElement result)
    {
        if (!result.TryGetProperty("meta", out var meta))
        {
            return null;
        }

        var currentPrice = ReadDecimal(meta, "regularMarketPrice");
        var previousClose = ReadDecimal(meta, "chartPreviousClose");
        var highDay = ReadDecimal(meta, "regularMarketDayHigh");
        var lowDay = ReadDecimal(meta, "regularMarketDayLow");
        var timestamp = ReadUnixTime(meta, "regularMarketTime") ?? DateTime.UtcNow;
        var quote = result
            .GetProperty("indicators")
            .GetProperty("quote")
            .EnumerateArray()
            .FirstOrDefault();

        if (currentPrice <= 0 && quote.ValueKind != JsonValueKind.Undefined)
        {
            currentPrice = ReadLastDecimal(quote, "close");
        }

        if (previousClose <= 0 && quote.ValueKind != JsonValueKind.Undefined)
        {
            previousClose = ReadFirstDecimal(quote, "close");
        }

        var openDay = quote.ValueKind == JsonValueKind.Undefined ? 0 : ReadLastDecimal(quote, "open");
        if (highDay <= 0 && quote.ValueKind != JsonValueKind.Undefined)
        {
            highDay = ReadLastDecimal(quote, "high");
        }

        if (lowDay <= 0 && quote.ValueKind != JsonValueKind.Undefined)
        {
            lowDay = ReadLastDecimal(quote, "low");
        }

        if (currentPrice <= 0)
        {
            return null;
        }

        if (previousClose <= 0)
        {
            previousClose = currentPrice;
        }

        var change = currentPrice - previousClose;
        var changePercent = previousClose == 0 ? 0 : change / previousClose * 100;
        var displaySymbol = ReadString(meta, "symbol") ?? symbol.ToUpperInvariant();

        return new StockQuote
        {
            Symbol = displaySymbol,
            CompanyName = ReadString(meta, "longName") ?? ReadString(meta, "shortName") ?? displaySymbol,
            CurrentPrice = Math.Round(currentPrice, 2),
            Change = Math.Round(change, 2),
            ChangePercent = Math.Round(changePercent, 2),
            HighDay = Math.Round(highDay, 2),
            LowDay = Math.Round(lowDay, 2),
            OpenDay = Math.Round(openDay, 2),
            PreviousClose = Math.Round(previousClose, 2),
            Timestamp = timestamp
        };
    }

    private static string NormalizeSymbol(string symbol)
    {
        return symbol.Trim().ToUpperInvariant();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static decimal ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static decimal ReadLastDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        foreach (var value in values.EnumerateArray().Reverse())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }
        }

        return 0;
    }

    private static decimal ReadFirstDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }
        }

        return 0;
    }

    private static DateTime? ReadUnixTime(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var seconds))
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

}
