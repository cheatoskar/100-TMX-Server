using System.Net;
using System.Net.Http.Json;
using System.Text;
using RandomizerAnywhere.Config;

namespace RandomizerAnywhere;

// How a server that is not the hub reaches the hub (see SyncHub): the shared event list and the
// shared leaderboard live there, in the hub's own files. Every call is short and bounded; a hub
// that doesn't answer is the caller's problem to handle (keep going on what it last had).
internal sealed class SyncClient
{
    public const string TokenHeader = "X-Sync-Token";

    private readonly AppConfig config;
    private readonly HttpClient http;

    public SyncClient(AppConfig config)
    {
        this.config = config;
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RandomizerAnywhere-sync");
    }

    // this server reads and writes the shared state through the hub
    public bool Enabled => !string.IsNullOrWhiteSpace(config.SyncHubUrl) && !string.IsNullOrWhiteSpace(config.SyncToken);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, config.SyncHubUrl + path);
        request.Headers.Add(TokenHeader, config.SyncToken);
        return request;
    }

    public async Task<Dictionary<string, LeaderboardEntry>> GetLeaderboardAsync(CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, "/sync/leaderboard"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Dictionary<string, LeaderboardEntry>>(cancellationToken) ?? [];
    }

    public async Task PostFinishesAsync(SyncFinish finish, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, "/sync/leaderboard/finish");
        request.Content = JsonContent.Create(finish);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // the event state as the hub has it: its revision and the raw JSON
    public async Task<string> GetEventStateAsync(CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, "/sync/event-state"), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    // Writes the state if the hub is still at baseRevision. Null = someone else wrote first.
    public async Task<long?> PutEventStateAsync(long baseRevision, string json, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Put, $"/sync/event-state?base={baseRevision}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<SyncRevision>(cancellationToken);
        return answer?.Revision ?? throw new InvalidOperationException("the hub answered without a revision");
    }
}

// Id: the hub counts a finish only once, even when an answer got lost and it is sent again
internal sealed record SyncFinish(string Login, string Nickname, string RawNickname, int Count = 1, string Id = "");

internal sealed record SyncRevision(long Revision);
