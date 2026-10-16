using RandomizerAnywhere.Config;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

namespace RandomizerAnywhere;

// Posts /imp and /hard reports to Discord - from a background queue, never from the game.
//
// The posts used to be awaited inside the chat-command handler, which holds the whole callback
// stream, on the shared HttpClient with its 40s budget and retries that wait out Discord's
// Retry-After. A burst of /imp hitting Discord's rate limit froze chat, checkpoints and map
// switches for everyone. Now a report is queued in microseconds and a single worker sends them.
internal sealed class DiscordNotifier
{
    private readonly AppConfig config;

    // Its own client: no retries (a POST that timed out after Discord accepted it was retried and
    // posted twice) and a short timeout.
    private static readonly HttpClient http = CreateHttpClient();

    // A handful of reports is normal; more than this waiting at once is a flood, and the extra
    // ones are dropped rather than queued up for minutes.
    private readonly Channel<(string WebhookUrl, string Content)> queue =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(20)
        {
            // Wait (never blocks: only TryWrite is used) - with DropWrite, TryWrite reports success
            // for a dropped item, so a flood would vanish without a word in the log
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    public DiscordNotifier(AppConfig config)
    {
        this.config = config;
        _ = Task.Run(SendLoopAsync);
    }

    public Task PostAsync(string content, CancellationToken cancellationToken = default)
    {
        Enqueue(config.DiscordWebhookUrl, content);
        return Task.CompletedTask;
    }

    // falls back to the main webhook if no dedicated "hard map" webhook is configured
    public Task PostHardAsync(string content, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(config.DiscordWebhookUrlHard) ? config.DiscordWebhookUrl : config.DiscordWebhookUrlHard;
        Enqueue(url, content);
        return Task.CompletedTask;
    }

    private void Enqueue(string webhookUrl, string content)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        // Discord rejects messages over 2000 characters outright
        if (content.Length > 1900)
        {
            // don't cut an emoji (surrogate pair) in half - the JSON writer refuses a lone half
            var cut = char.IsHighSurrogate(content[1899]) ? 1899 : 1900;
            content = content[..cut] + "...";
        }

        if (!queue.Writer.TryWrite((webhookUrl, content)))
        {
            Console.WriteLine("Warning: Discord queue is full - dropped a report.");
        }
    }

    private async Task SendLoopAsync()
    {
        await foreach (var (webhookUrl, content) in queue.Reader.ReadAllAsync())
        {
            try
            {
                await SendAsync(webhookUrl, content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to post Discord notification - {ex.Message}");
            }
        }
    }

    private static async Task SendAsync(string webhookUrl, string content)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            // allowed_mentions with an empty list: whatever ends up in the text - a player calling
            // themselves "@everyone" - can never ping anyone
            using var response = await http.PostAsJsonAsync(webhookUrl, new
            {
                content,
                allowed_mentions = new { parse = Array.Empty<string>() },
            });

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                await Task.Delay(wait < MaxRetryAfter ? wait : MaxRetryAfter);
                continue;
            }

            // a deleted webhook or a rejected message used to fail without a trace
            Console.WriteLine($"Warning: Discord refused a notification - HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            return;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RandomizerAnywhere-100TMX (+https://github.com/cheatoskar/randomizer-anywhere-100--tmx)");
        return client;
    }

    // A player's name as it may appear in a Discord message: TrackMania formatting and links
    // removed, then shown as inline code so Discord renders it literally - no markdown, no
    // clickable links, no mentions.
    public static string FormatPlayerName(string plainNickname)
    {
        var name = plainNickname.Replace('`', '\'').Replace("\r", " ").Replace("\n", " ").Trim();

        if (name.Length > 40)
        {
            name = name[..(char.IsHighSurrogate(name[39]) ? 39 : 40)] + "...";
        }

        return name.Length == 0 ? "`(no name)`" : $"`{name}`";
    }
}
