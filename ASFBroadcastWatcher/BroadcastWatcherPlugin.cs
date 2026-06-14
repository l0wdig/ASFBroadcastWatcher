// ASFBroadcastWatcher - ASF plugin to watch Steam broadcasts for drop rewards
// Supports multiple bot accounts simultaneously via the BCAST command

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;

namespace ASFBroadcastWatcher;

[Export(typeof(IPlugin))]
internal sealed class BroadcastWatcherPlugin : IPlugin, IBotCommand2 {

    public string Name => "ASFBroadcastWatcher";
    public Version Version => typeof(BroadcastWatcherPlugin).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

    internal static readonly ConcurrentDictionary<string, WatchSession> ActiveSessions = new(StringComparer.OrdinalIgnoreCase);

    public Task OnLoaded() {
        ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded. Commands: BCAST <Bots> <BroadcastUrl> | BCASTSTOP <Bots> | BCASTLIST");
        return Task.CompletedTask;
    }

    public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
        if (args.Length == 0) return null;

        switch (args[0].ToUpperInvariant()) {

            case "BCAST" when access >= EAccess.Master: {
                if (args.Length < 3) {
                    return "Usage: BCAST <BotNames|ASF> <BroadcastUrl>\n" +
                           "Example: BCAST account1,account2 https://steamcommunity.com/broadcast/watch/76561198888084799";
                }

                string botNames = args[1];
                string url = args[2];

                if (!url.Contains("steamcommunity.com/broadcast/watch/", StringComparison.OrdinalIgnoreCase)) {
                    return "❌ Invalid broadcast URL. Expected: https://steamcommunity.com/broadcast/watch/<SteamID64>";
                }

                string? broadcasterSteamId = ExtractSteamIdFromUrl(url);
                if (broadcasterSteamId == null) {
                    return $"❌ Could not parse SteamID64 from URL: {url}";
                }

                HashSet<Bot>? bots = Bot.GetBots(botNames);
                if (bots == null || bots.Count == 0) {
                    return $"❌ No bots found matching: {botNames}";
                }

                IEnumerable<Task<string>> tasks = bots.Select(b => StartWatchingAsync(b, broadcasterSteamId, url));
                string[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return string.Join("\n", results);
            }

            case "BCASTSTOP" when access >= EAccess.Master: {
                if (args.Length < 2) return "Usage: BCASTSTOP <BotNames|ASF>";

                HashSet<Bot>? bots = Bot.GetBots(args[1]);
                if (bots == null || bots.Count == 0) return $"❌ No bots found matching: {args[1]}";

                List<string> results = new();
                foreach (Bot b in bots) {
                    if (ActiveSessions.TryRemove(b.BotName, out WatchSession? session)) {
                        await session.StopAsync().ConfigureAwait(false);
                        results.Add($"✅ {b.BotName}: Stopped watching broadcast.");
                    } else {
                        results.Add($"ℹ️ {b.BotName}: No active broadcast session.");
                    }
                }
                return string.Join("\n", results);
            }

            case "BCASTLIST" when access >= EAccess.Operator: {
                if (ActiveSessions.IsEmpty) return "ℹ️ No active broadcast sessions.";

                List<string> lines = new() { "📺 Active broadcast sessions:" };
                foreach ((string botName, WatchSession session) in ActiveSessions) {
                    TimeSpan elapsed = DateTime.UtcNow - session.StartedAt;
                    lines.Add($"  • {botName} → SteamID {session.BroadcasterSteamId} | {(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s elapsed");
                }
                return string.Join("\n", lines);
            }

            default:
                return null;
        }
    }

    private static async Task<string> StartWatchingAsync(Bot bot, string broadcasterSteamId, string originalUrl) {
        if (!bot.IsConnectedAndLoggedOn) {
            return $"❌ {bot.BotName}: Bot is not logged on.";
        }

        if (ActiveSessions.TryRemove(bot.BotName, out WatchSession? existing)) {
            await existing.StopAsync().ConfigureAwait(false);
        }

        try {
            // Step 1: Load the broadcast watch page to establish session cookies
            Uri watchUri = new(originalUrl);
            HtmlDocumentResponse? watchPage = await bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(
                watchUri,
                referer: new Uri("https://steamcommunity.com/")
            ).ConfigureAwait(false);

            if (watchPage == null) {
                return $"❌ {bot.BotName}: Failed to load broadcast page.";
            }

            // Step 2: Call getbroadcastmpd to get the broadcastid (sessionid) and viewertoken
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Calling getbroadcastmpd for {broadcasterSteamId}...");
            BroadcastMpdResponse? mpd = await GetBroadcastMpdAsync(bot, broadcasterSteamId).ConfigureAwait(false);

            if (mpd == null || mpd.Success == 0 || string.IsNullOrEmpty(mpd.BroadcastId) || mpd.BroadcastId == "0") {
                bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: getbroadcastmpd returned success={mpd?.Success} broadcastid={mpd?.BroadcastId}");
                return $"❌ {bot.BotName}: Could not get broadcast session from Steam. Is the broadcast actually live?";
            }

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Got broadcastid={mpd.BroadcastId} viewertoken={mpd.ViewerToken}");

            WatchSession session = new(bot, broadcasterSteamId, mpd.BroadcastId, mpd.ViewerToken);
            ActiveSessions[bot.BotName] = session;
            session.Start();

            return $"✅ {bot.BotName}: Now watching broadcast by {broadcasterSteamId}. Heartbeating every 30s. Use BCASTSTOP {bot.BotName} to stop.";
        } catch (Exception ex) {
            return $"❌ {bot.BotName}: Exception — {ex.Message}";
        }
    }

    // Calls the community endpoint to register as a viewer and get a session token
    internal static async Task<BroadcastMpdResponse?> GetBroadcastMpdAsync(Bot bot, string broadcasterSteamId, string viewerToken = "0", string broadcastId = "0") {
        Uri mpdUri = new("https://steamcommunity.com/broadcast/getbroadcastmpd/");

        // cellid tells Steam which CDN cell (region) we're in — without it the session
        // is considered invalid almost immediately, causing "broadcast offline" errors.
        // ASF exposes this via the static ASF.GlobalDatabase; fall back to 0 if unavailable.
        uint cellId = ASF.GlobalDatabase?.CellID ?? 0;

        Dictionary<string, string> data = new() {
            { "steamid", broadcasterSteamId },
            { "broadcastid", broadcastId },
            { "viewertoken", viewerToken },
            { "appid", "0" },
            { "playerid", "0" },
            { "cellid", cellId.ToString() },
            { "langpref", "english" },
            { "maxresolution", "1080" },
        };

        ObjectResponse<BroadcastMpdResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<BroadcastMpdResponse>(
            mpdUri,
            data: data,
            referer: new Uri($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}")
        ).ConfigureAwait(false);

        if (response?.Content != null) {
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] getbroadcastmpd: success={response.Content.Success} broadcastid={response.Content.BroadcastId} viewertoken={response.Content.ViewerToken}");
        } else {
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] getbroadcastmpd returned null for {broadcasterSteamId}");
        }

        return response?.Content;
    }

    // Sends a heartbeat using the OFFICIAL Steam Web API endpoint with correct parameter names
    // GET https://api.steampowered.com/ISteamBroadcast/ViewerHeartbeat/v1/
    // Parameters: steamid, sessionid (=broadcastid), token (=viewertoken), stream
    internal static async Task<HeartbeatResponse?> SendHeartbeatAsync(Bot bot, string broadcasterSteamId, string sessionId, string token) {
        // Build the query string manually — this is a GET request to the Web API, not a POST to community
        Uri heartbeatUri = new(
            $"https://api.steampowered.com/ISteamBroadcast/ViewerHeartbeat/v1/" +
            $"?steamid={Uri.EscapeDataString(broadcasterSteamId)}" +
            $"&sessionid={Uri.EscapeDataString(sessionId)}" +
            $"&token={Uri.EscapeDataString(token)}" +
            $"&stream=0"
        );

        ObjectResponse<HeartbeatResponse>? response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<HeartbeatResponse>(
            heartbeatUri,
            referer: new Uri($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}"),
            requestOptions: ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnClientErrors | ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnServerErrors
        ).ConfigureAwait(false);

        if (response?.Content == null) {
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Heartbeat (Web API) returned null");
            return null;
        }

        bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Heartbeat result={response.Content.Result} token={response.Content.Token}");
        return response.Content;
    }

    private static string? ExtractSteamIdFromUrl(string url) {
        int idx = url.IndexOf("/broadcast/watch/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) {
            string remainder = url[(idx + "/broadcast/watch/".Length)..].TrimEnd('/').Split('?')[0];
            if (remainder.Length > 0 && remainder.All(char.IsDigit)) {
                return remainder;
            }
        }
        return null;
    }
}

// ──────────────────────────────────────────────
// WatchSession: manages the heartbeat loop for one bot
// ──────────────────────────────────────────────

internal sealed class WatchSession {
    internal readonly string BroadcasterSteamId;
    internal readonly DateTime StartedAt;

    private readonly Bot _bot;
    private string _sessionId;   // = broadcastid from getbroadcastmpd
    private string _token;       // = viewertoken from getbroadcastmpd / heartbeat response
    private readonly CancellationTokenSource _cts = new();

    // Steam expects heartbeats every ~20 seconds; 30s is sometimes too slow
    // and causes the server to mark the session as stale
    private const int HeartbeatIntervalSeconds = 20;

    internal WatchSession(Bot bot, string broadcasterSteamId, string sessionId, string token) {
        _bot = bot;
        BroadcasterSteamId = broadcasterSteamId;
        _sessionId = sessionId;
        _token = token;
        StartedAt = DateTime.UtcNow;
    }

    internal void Start() => _ = HeartbeatLoopAsync(_cts.Token);
    internal async Task StopAsync() => await _cts.CancelAsync().ConfigureAwait(false);

    private async Task HeartbeatLoopAsync(CancellationToken ct) {
        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat loop started for broadcaster {BroadcasterSteamId}.");

        try {
            while (!ct.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                HeartbeatResponse? heartbeat = await BroadcastWatcherPlugin.SendHeartbeatAsync(
                    _bot, BroadcasterSteamId, _sessionId, _token
                ).ConfigureAwait(false);

                if (heartbeat == null || heartbeat.Result != 1) {
                    // Heartbeat failed — reload watch page to refresh session cookies, then re-register
                    _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat failed (result={heartbeat?.Result}), reloading page and re-registering...");

                    // Re-establish session cookies before calling getbroadcastmpd
                    Uri watchUri = new($"https://steamcommunity.com/broadcast/watch/{BroadcasterSteamId}");
                    await _bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(
                        watchUri,
                        referer: new Uri("https://steamcommunity.com/")
                    ).ConfigureAwait(false);

                    BroadcastMpdResponse? mpd = await BroadcastWatcherPlugin.GetBroadcastMpdAsync(
                        _bot, BroadcasterSteamId, "0", "0"
                    ).ConfigureAwait(false);

                    if (mpd != null && mpd.Success == 1 && !string.IsNullOrEmpty(mpd.BroadcastId) && mpd.BroadcastId != "0") {
                        _sessionId = mpd.BroadcastId;
                        _token = mpd.ViewerToken;
                        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Re-registered. New sessionid={_sessionId}");
                        continue;
                    }

                    _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Broadcast ended or unavailable. Stopping.");
                    BroadcastWatcherPlugin.ActiveSessions.TryRemove(_bot.BotName, out _);
                    break;
                }

                // Update token if Steam gave us a new one
                if (!string.IsNullOrEmpty(heartbeat.Token) && heartbeat.Token != "0") {
                    _token = heartbeat.Token;
                }

                _bot.ArchiLogger.LogGenericDebug($"[BroadcastWatcher] {_bot.BotName}: Heartbeat OK.");
            }
        } catch (OperationCanceledException) {
            // Normal stop via BCASTSTOP
        } catch (Exception ex) {
            _bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {_bot.BotName}: Unexpected error in heartbeat loop.");
            BroadcastWatcherPlugin.ActiveSessions.TryRemove(_bot.BotName, out _);
        }

        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat loop stopped.");
    }
}

// ──────────────────────────────────────────────
// JSON response models
// ──────────────────────────────────────────────

internal sealed class BroadcastMpdResponse {
    [JsonPropertyName("success")]
    public int Success { get; init; }

    [JsonPropertyName("broadcastid")]
    public string BroadcastId { get; init; } = "0";

    [JsonPropertyName("viewertoken")]
    public string ViewerToken { get; init; } = "0";

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

internal sealed class HeartbeatResponse {
    // The official ISteamBroadcast/ViewerHeartbeat/v1 returns "result" not "success"
    [JsonPropertyName("result")]
    public int Result { get; init; }

    // And "token" not "viewertoken"
    [JsonPropertyName("token")]
    public string Token { get; init; } = "0";
}
