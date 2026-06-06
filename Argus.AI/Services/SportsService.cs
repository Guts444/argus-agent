using System.Net.Http.Json;
using System.Text.Json;
using Argus.Core.Models;
using Argus.Core.Services;

namespace Argus.AI.Services;

/// <summary>
/// Sports scores via ESPN's free public API.
/// </summary>
public class SportsService : ISportsService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public SportsService(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<SportScore>> GetScoresAsync(
        string league, IEnumerable<string>? teamIds = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://site.api.espn.com/apis/site/v2/sports/soccer/{league}/scoreboard";
            var response = await _http.GetFromJsonAsync<EspnScoreboard>(url, JsonOpts, ct);
            if (response?.Events is null) return Array.Empty<SportScore>();

            if (teamIds?.Any() == true)
            {
                var idSet = teamIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                response.Events = response.Events
                    .Where(evt => EventMatchesTeams(evt, idSet))
                    .ToList();
            }

            return response.Events.Select(MapEvent).ToList();
        }
        catch { return Array.Empty<SportScore>(); }
    }

    private static bool EventMatchesTeams(EspnEvent evt, HashSet<string> teamIds)
    {
        if (teamIds.Contains(evt.Id ?? ""))
        {
            return true;
        }

        return evt.Competitions?
            .SelectMany(comp => comp.Competitors ?? [])
            .Any(competitor =>
                teamIds.Contains(competitor.Team?.Id ?? "") ||
                teamIds.Contains(competitor.Team?.DisplayName ?? "") ||
                teamIds.Contains(competitor.Team?.ShortDisplayName ?? "")) == true;
    }

    private static SportScore MapEvent(EspnEvent evt)
    {
        var comp = evt.Competitions?.FirstOrDefault();
        var home = comp?.Competitors?.FirstOrDefault(c => c.HomeAway == "home");
        var away = comp?.Competitors?.FirstOrDefault(c => c.HomeAway == "away");
        var status = comp?.Status?.Type?.ShortDetail ??
            comp?.Status?.Type?.Description ??
            comp?.Status?.Type?.Name ??
            "Scheduled";

        return new SportScore
        {
            Id = evt.Id ?? "",
            League = evt.League ?? "Soccer",
            HomeTeam = home?.Team?.DisplayName ?? home?.Team?.ShortDisplayName ?? "TBD",
            AwayTeam = away?.Team?.DisplayName ?? away?.Team?.ShortDisplayName ?? "TBD",
            HomeScore = TryParseScore(home?.Score),
            AwayScore = TryParseScore(away?.Score),
            Status = status,
            Period = comp?.Status?.Period?.ToString(),
            StartTime = evt.Date,
            HomeLogoUrl = home?.Team?.Logo,
            AwayLogoUrl = away?.Team?.Logo
        };
    }

    private static int? TryParseScore(string? score)
    {
        return int.TryParse(score, out var parsed) ? parsed : null;
    }

    private class EspnScoreboard
    {
        public List<EspnEvent>? Events { get; set; }
    }

    private class EspnEvent
    {
        public string? Id { get; set; }
        public string? League { get; set; }
        public DateTime Date { get; set; }
        public List<EspnCompetition>? Competitions { get; set; }
    }

    private class EspnCompetition
    {
        public List<EspnCompetitor>? Competitors { get; set; }
        public EspnStatus? Status { get; set; }
    }

    private class EspnCompetitor
    {
        public string? HomeAway { get; set; }
        public string? Score { get; set; }
        public EspnTeam? Team { get; set; }
    }

    private class EspnTeam
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? ShortDisplayName { get; set; }
        public string? Logo { get; set; }
    }

    private class EspnStatus
    {
        public int? Period { get; set; }
        public EspnType? Type { get; set; }
    }

    private class EspnType
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? ShortDetail { get; set; }
    }
}
