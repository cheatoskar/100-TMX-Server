using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using RandomizerAnywhere.Config;

namespace RandomizerAnywhere;

// The hub: the server whose files hold the shared leaderboard and the shared open-maps/event state
// for servers on other machines (they talk to it through SyncClient). Runs on the status page's
// port, under /sync/. Only with SyncToken set and SyncHubUrl empty (a server is a hub or a client,
// never both); every request needs the token, and with SyncAllowedIps set only those addresses get
// an answer at all - anything else sees the same 404 as a path that doesn't exist.
internal sealed class SyncHub
{
    // the event state with a few hundred maps and finishes is ~100 KB
    private const long MaxBodyBytes = 2_000_000;

    private readonly AppConfig config;
    private readonly Leaderboard leaderboard;
    private readonly EventMode eventMode;
    private readonly byte[] token;
    private readonly HashSet<IPAddress> allowed;

    public SyncHub(AppConfig config, Leaderboard leaderboard, EventMode eventMode)
    {
        this.config = config;
        this.leaderboard = leaderboard;
        this.eventMode = eventMode;
        token = Encoding.UTF8.GetBytes(config.SyncToken);
        allowed = [.. config.SyncAllowedIps
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(text => IPAddress.TryParse(text, out var ip) ? ip : null)
            .OfType<IPAddress>()];
    }

    public bool Enabled => string.IsNullOrWhiteSpace(config.SyncHubUrl) && config.SyncToken.Length >= 24;

    public void Map(WebApplication app)
    {
        if (!Enabled)
        {
            if (string.IsNullOrWhiteSpace(config.SyncHubUrl) && config.SyncToken.Length > 0)
            {
                Console.WriteLine("Warning: SyncToken is shorter than 24 characters - the sync hub stays off.");
            }

            return;
        }

        Console.WriteLine($"Sync hub on /sync/ - {(allowed.Count > 0 ? $"only for {string.Join(", ", allowed)}" : "for any address with the token")}.");

        app.MapGet("/sync/leaderboard", (HttpContext context) =>
            Authorized(context) ? Results.Json(leaderboard.Snapshot()) : Results.NotFound());

        app.MapPost("/sync/leaderboard/finish", async (HttpContext context) =>
        {
            if (!Authorized(context))
            {
                return Results.NotFound();
            }

            // SyncClient sends it with the web defaults (camelCase)
            var finish = JsonSerializer.Deserialize<SyncFinish>(await ReadBodyAsync(context), JsonSerializerOptions.Web);
            if (finish is null || string.IsNullOrWhiteSpace(finish.Login) || finish.Login.Length > 64)
            {
                return Results.BadRequest();
            }

            await leaderboard.AddRemoteFinishesAsync(finish);
            return Results.Ok();
        });

        app.MapGet("/sync/event-state", (HttpContext context) =>
            Authorized(context) ? Results.Content(eventMode.ExportStateJson(), "application/json") : Results.NotFound());

        app.MapPut("/sync/event-state", async (HttpContext context) =>
        {
            if (!Authorized(context))
            {
                return Results.NotFound();
            }

            if (!long.TryParse(context.Request.Query["base"], out var baseRevision))
            {
                return Results.BadRequest();
            }

            try
            {
                var written = await eventMode.ImportStateAsync(await ReadBodyAsync(context), baseRevision);
                return written is { } revision ? Results.Json(new SyncRevision(revision)) : Results.Conflict();
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
        });
    }

    private bool Authorized(HttpContext context)
    {
        if (allowed.Count > 0 && (context.Connection.RemoteIpAddress is not { } ip
            || !allowed.Contains(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)))
        {
            return false;
        }

        var sent = Encoding.UTF8.GetBytes(context.Request.Headers[SyncClient.TokenHeader].ToString());
        return CryptographicOperations.FixedTimeEquals(sent, token);
    }

    // the web server takes no request bodies (MaxRequestBodySize = 0); these few may have one
    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(context.RequestAborted);
    }
}
