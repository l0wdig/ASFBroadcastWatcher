// ASFBroadcastWatcher - ASF plugin to watch Steam broadcasts for drop rewards
// Supports multiple bot accounts simultaneously via the BCAST command

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

    // Tracks active watch sessions per bot: botName -> WatchSession
    internal static readonly ConcurrentDictionary<string, WatchSession> ActiveSessions = new(StringComparer.OrdinalIgnoreCase);

    public Task OnLoaded() {
        ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded. Commands: BCAST <Bots> <BroadcastUrl> | BCASTSTOP <Bots> | BCASTLIST");
        return Task.CompletedTask;
    }

    public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
        if (args.Length == 0) {
            return null;
        }

        switch (args[0].ToUpperInvariant()) {

            // BCAST <BotNames|ASF> <BroadcastUrl>
            // Start watching a broadcast URL on one or more bots
            case "BCAST" when access >= EAccess.Master: {
                if (args.Length < 3) {
                    return "Usage: BCAST <BotNames|ASF> <BroadcastUrl>\n" +
                           "Example: BCAST account1,account2 https://steamcommunity.com/broadcast/watch/76561198888084799";
                }

                string botNames = args[1];
                string url = args[2];

                // Validate it looks like a Steam broadcast URL
                if (!url.Contains("steamcommunity.com/broadcast/watch/", StringComparison.OrdinalIgnoreCase) &&
                    !url.Contains("steam.tv/", StringComparison.OrdinalIgnoreCase)) {
                    return $"❌ Invalid broadcast URL. Expected: https://steamcommunity.com/broadcast/watch/<SteamID64>";
                }

                // Extract SteamID64 from URL
                string? broadcasterSteamId = ExtractSteamIdFromUrl(url);
                if (broadcasterSteamId == null) {
                    return $"❌ Could not parse SteamID64 from URL: {url}";
                }

                HashSet<Bot>? bots = Bot.GetBots(botNames);
                if (bots == null || bots.Count == 0) {
                    return $"❌ No bots found matching: {botNames}";
                }

                // Fire off watching on all bots in parallel, collect results
                IEnumerable<Task<string>> tasks = bots.Select(b => StartWatchingAsync(b, broadcasterSteamId, url));
                string[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return string.Join("\n", results);
            }

            // BCASTSTOP <BotNames|ASF>
            // Stop watching on one or more bots
            case "BCASTSTOP" when access >= EAccess.Master: {
                if (args.Length < 2) {
                    return "Usage: BCASTSTOP <BotNames|ASF>";
                }

                string botNames = args[1];
                HashSet<Bot>? bots = Bot.GetBots(botNames);
                if (bots == null || bots.Count == 0) {
                    return $"❌ No bots found matching: {botNames}";
                }

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

            // BCASTLIST
            // Show all currently active watch sessions
            case "BCASTLIST" when access >= EAccess.Operator: {
                if (ActiveSessions.IsEmpty) {
                    return "ℹ️ No active broadcast sessions.";
                }

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

        // Cancel any existing session for this bot
        if (ActiveSessions.TryRemove(bot.BotName, out WatchSession? existing)) {
            await existing.StopAsync().ConfigureAwait(false);
        }

        try {
            // Load the broadcast page to set session cookies
            Uri watchUri = new(originalUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? originalUrl
                : "https://steamcommunity.com/broadcast/watch/" + broadcasterSteamId);

            HtmlDocumentResponse? watchPage = await bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(
                watchUri,
                referer: new Uri("https://steamcommunity.com/")
            ).ConfigureAwait(false);

            if (watchPage == null) {
                return $"❌ {bot.BotName}: Failed to load broadcast page.";
            }

            // FIX: Call getbroadcastmpd first to get the real broadcastid and viewertoken
            // Without these, the heartbeat endpoint returns an error JSON that can't be parsed
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Fetching broadcast MPD for {broadcasterSteamId}...");
            BroadcastMpdResponse? mpd = await GetBroadcastMpdAsync(bot, broadcasterSteamId).ConfigureAwait(false);

            if (mpd == null || mpd.Success == 0) {
                return $"❌ {bot.BotName}: Could not get broadcast session from Steam (getbroadcastmpd failed). Is the broadcast actually live?";
            }

            string broadcastId = mpd.BroadcastId;
            string viewerToken = mpd.ViewerToken;

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Got broadcastid={broadcastId} viewertoken={viewerToken}");

            WatchSession session = new(bot, broadcasterSteamId, broadcastId, viewerToken);
            ActiveSessions[bot.BotName] = session;
            session.Start();

            return $"✅ {bot.BotName}: Now watching broadcast by {broadcasterSteamId}. Heartbeating every 60s. Use BCASTSTOP {bot.BotName} to stop.";
        } catch (Exception ex) {
            return $"❌ {bot.BotName}: Exception — {ex.Message}";
        }
    }

    internal static async Task<BroadcastMpdResponse?> GetBroadcastMpdAsync(Bot bot, string broadcasterSteamId, string viewerToken = "0", string broadcastId = "0") {
        Uri mpdUri = new("https://steamcommunity.com/broadcast/getbroadcastmpd/");

        Dictionary<string, string> data = new() {
            { "steamid", broadcasterSteamId },
            { "broadcastid", broadcastId },
            { "viewertoken", viewerToken },
        };

        ObjectResponse<BroadcastMpdResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<BroadcastMpdResponse>(
            mpdUri,
            data: data,
            referer: new Uri($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}")
        ).ConfigureAwait(false);

        if (response?.Content != null) {
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] getbroadcastmpd success={response.Content.Success} broadcastid={response.Content.BroadcastId} viewertoken={response.Content.ViewerToken}");
        } else {
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] getbroadcastmpd returned null for {broadcasterSteamId}");
        }

        return response?.Content;
    }

    internal static async Task<HeartbeatResponse?> SendHeartbeatAsync(Bot bot, string broadcasterSteamId, string broadcastId, string viewerToken) {
        Uri heartbeatUri = new("https://steamcommunity.com/broadcast/heartbeat/");

        Dictionary<string, string> data = new() {
            { "steamid", broadcasterSteamId },
            { "broadcastid", broadcastId },
            { "viewertoken", viewerToken },
        };

        ObjectResponse<HeartbeatResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<HeartbeatResponse>(
            heartbeatUri,
            data: data,
            referer: new Uri($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}"),
            requestOptions: ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnClientErrors | ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnServerErrors
        ).ConfigureAwait(false);

        if (response?.Content == null) {
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Heartbeat returned null (HTTP status may indicate error)");
            return null;
        }

        bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Heartbeat success={response.Content.Success} viewertoken={response.Content.ViewerToken}");
        return response.Content;
    }

    private static string? ExtractSteamIdFromUrl(string url) {
        // Handles: https://steamcommunity.com/broadcast/watch/76561198888084799
        int idx = url.IndexOf("/broadcast/watch/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) {
            string remainder = url[(idx + "/broadcast/watch/".Length)..].TrimEnd('/').Split('?')[0];
            if (remainder.Length > 0 && remainder.All(char.IsDigit)) {
                return remainder;
            }
        }

        // Handles: https://steam.tv/somevanityname — we can't extract SteamID64 from a vanity name here,
        // user should use the /broadcast/watch/<steamid64> form.
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
    // FIX: these must be non-readonly so Steam's updated viewertoken can be saved each heartbeat
    private string _broadcastId;
    private string _viewerToken;
    private readonly CancellationTokenSource _cts = new();

    private const int HeartbeatIntervalSeconds = 60;

    internal WatchSession(Bot bot, string broadcasterSteamId, string broadcastId, string viewerToken) {
        _bot = bot;
        BroadcasterSteamId = broadcasterSteamId;
        _broadcastId = broadcastId;
        _viewerToken = viewerToken;
        StartedAt = DateTime.UtcNow;
    }

    internal void Start() {
        // Run the heartbeat loop in the background; don't await it here
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    internal async Task StopAsync() {
        await _cts.CancelAsync().ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct) {
        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat loop started for broadcaster {BroadcasterSteamId}.");

        try {
            while (!ct.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) {
                    break;
                }

                HeartbeatResponse? heartbeat = await BroadcastWatcherPlugin.SendHeartbeatAsync(_bot, BroadcasterSteamId, _broadcastId, _viewerToken).ConfigureAwait(false);

                if (heartbeat == null || heartbeat.Success != 1) {
                    _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Broadcast by {BroadcasterSteamId} has ended or become unavailable. Stopping.");
                    BroadcastWatcherPlugin.ActiveSessions.TryRemove(_bot.BotName, out _);
                    break;
                }

                // FIX: Steam may return a refreshed viewertoken — save it for the next heartbeat
                if (!string.IsNullOrEmpty(heartbeat.ViewerToken) && heartbeat.ViewerToken != "0") {
                    _viewerToken = heartbeat.ViewerToken;
                }

                _bot.ArchiLogger.LogGenericDebug($"[BroadcastWatcher] {_bot.BotName}: Heartbeat OK (broadcaster={BroadcasterSteamId}).");
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
    [JsonPropertyName("success")]
    public int Success { get; init; }

    // FIX: capture the refreshed viewertoken Steam sometimes returns in the heartbeat response
    [JsonPropertyName("viewertoken")]
    public string ViewerToken { get; init; } = "0";
}
