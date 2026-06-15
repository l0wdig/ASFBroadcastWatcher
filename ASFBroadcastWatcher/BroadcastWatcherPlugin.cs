// ASFBroadcastWatcher - ASF plugin to watch Steam broadcasts for drop rewards
// Supports multiple bot accounts simultaneously via the BCAST command

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
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
            Uri watchUri = new($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}");

            // Step 1: Load the broadcast watch page to establish cookies/session
            HtmlDocumentResponse? watchPage = await bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(
                watchUri,
                referer: new Uri("https://steamcommunity.com/")
            ).ConfigureAwait(false);

            if (watchPage == null) {
                return $"❌ {bot.BotName}: Failed to load broadcast page.";
            }

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Loaded broadcast page, now calling getbroadcastmpd...");

            // Step 2: Call getbroadcastmpd with all required parameters
            // appid=0 tells Steam we don't filter by game, watchlocation=1 = community page viewer
            BroadcastMpdResponse? mpd = await GetBroadcastMpdAsync(bot, broadcasterSteamId, "0", "0").ConfigureAwait(false);

            if (mpd == null) {
                return $"❌ {bot.BotName}: No response from Steam (getbroadcastmpd). Check ASF logs for details.";
            }

            // success=1 means live, success=2 means we need to retry/wait, other values = not live
            if (mpd.Success != 1) {
                return $"❌ {bot.BotName}: Steam returned success={mpd.Success} — broadcast may not be live or accessible. (Is it public? Are you friends with the broadcaster?)";
            }

            string broadcastId = mpd.BroadcastId;
            string viewerToken = mpd.ViewerToken;

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Session acquired — broadcastid={broadcastId} viewertoken={viewerToken}");

            WatchSession session = new(bot, broadcasterSteamId, broadcastId, viewerToken);
            ActiveSessions[bot.BotName] = session;
            session.Start();

            return $"✅ {bot.BotName}: Now watching broadcast by {broadcasterSteamId}. Use BCASTSTOP {bot.BotName} to stop.";
        } catch (Exception ex) {
            return $"❌ {bot.BotName}: Exception — {ex.Message}";
        }
    }

    internal static async Task<BroadcastMpdResponse?> GetBroadcastMpdAsync(Bot bot, string broadcasterSteamId, string viewerToken, string broadcastId) {
        Uri mpdUri = new("https://steamcommunity.com/broadcast/getbroadcastmpd/");

        // These are all the parameters the Steam web client sends when opening a broadcast watch page
        Dictionary<string, string> data = new() {
            { "steamid", broadcasterSteamId },
            { "broadcastid", broadcastId },
            { "viewertoken", viewerToken },
            { "appid", "0" },           // 0 = any game; without this Steam may reject the request
            { "watchlocation", "1" },   // 1 = community page; required by Steam's endpoint
            { "playerid", "0" },        // video player instance id
            { "offset", "0" },          // stream offset in seconds
        };

        ObjectResponse<BroadcastMpdResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<BroadcastMpdResponse>(
            mpdUri,
            data: data,
            referer: new Uri($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}"),
            requestOptions: ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnClientErrors | ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnServerErrors
        ).ConfigureAwait(false);

        if (response?.Content != null) {
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: getbroadcastmpd → success={response.Content.Success} broadcastid={response.Content.BroadcastId} viewertoken={response.Content.ViewerToken}");
        } else {
            bot.ArchiLogger.LogGenericWarning($"[BroadcastWatcher] {bot.BotName}: getbroadcastmpd returned null/unparseable response");
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
            bot.ArchiLogger.LogGenericWarning($"[BroadcastWatcher] {bot.BotName}: Heartbeat got null/unparseable response");
            return null;
        }

        bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Heartbeat → success={response.Content.Success} viewertoken={response.Content.ViewerToken}");
        return response.Content;
    }

    private static string? ExtractSteamIdFromUrl(string url) {
        int idx = url.IndexOf("/broadcast/watch/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) {
            string remainder = url[(idx + "/broadcast/watch/".Length)..].TrimEnd('/').Split('?')[0];
            if (remainder.Length > 0 && remainder.All(char.IsDigit)) return remainder;
        }
        return null;
    }
}

// ──────────────────────────────────────────────
// WatchSession: heartbeat loop for one bot
// ──────────────────────────────────────────────

internal sealed class WatchSession {
    internal readonly string BroadcasterSteamId;
    internal readonly DateTime StartedAt;

    private readonly Bot _bot;
    private string _broadcastId;
    private string _viewerToken;
    private readonly CancellationTokenSource _cts = new();

    // Steam expects a heartbeat every 30-60 seconds from the browser;
    // we use 30s to stay well within the window
    private const int HeartbeatIntervalSeconds = 30;

    // If heartbeat fails, retry a few times before giving up
    private const int MaxConsecutiveFailures = 5;

    internal WatchSession(Bot bot, string broadcasterSteamId, string broadcastId, string viewerToken) {
        _bot = bot;
        BroadcasterSteamId = broadcasterSteamId;
        _broadcastId = broadcastId;
        _viewerToken = viewerToken;
        StartedAt = DateTime.UtcNow;
    }

    internal void Start() => _ = HeartbeatLoopAsync(_cts.Token);

    internal async Task StopAsync() => await _cts.CancelAsync().ConfigureAwait(false);

    private async Task HeartbeatLoopAsync(CancellationToken ct) {
        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat loop started for broadcaster {BroadcasterSteamId}.");
        int failures = 0;

        try {
            while (!ct.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                HeartbeatResponse? heartbeat = await BroadcastWatcherPlugin.SendHeartbeatAsync(
                    _bot, BroadcasterSteamId, _broadcastId, _viewerToken
                ).ConfigureAwait(false);

                if (heartbeat == null) {
                    failures++;
                    _bot.ArchiLogger.LogGenericWarning($"[BroadcastWatcher] {_bot.BotName}: Heartbeat failed ({failures}/{MaxConsecutiveFailures})");

                    if (failures >= MaxConsecutiveFailures) {
                        _bot.ArchiLogger.LogGenericWarning($"[BroadcastWatcher] {_bot.BotName}: Too many failures, stopping.");
                        break;
                    }
                    continue;
                }

                if (heartbeat.Success != 1) {
                    _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Broadcast ended (success={heartbeat.Success}). Stopping.");
                    break;
                }

                // Reset failure counter on success
                failures = 0;

                // Save refreshed viewertoken if Steam sends one back
                if (!string.IsNullOrEmpty(heartbeat.ViewerToken) && heartbeat.ViewerToken != "0") {
                    _viewerToken = heartbeat.ViewerToken;
                }

                _bot.ArchiLogger.LogGenericDebug($"[BroadcastWatcher] {_bot.BotName}: Heartbeat OK ✓");
            }
        } catch (OperationCanceledException) {
            // Normal stop via BCASTSTOP — not an error
        } catch (Exception ex) {
            _bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {_bot.BotName}: Unexpected error in heartbeat loop.");
        }

        BroadcastWatcherPlugin.ActiveSessions.TryRemove(_bot.BotName, out _);
        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat loop stopped.");
    }
}

// ──────────────────────────────────────────────
// JSON models
// ──────────────────────────────────────────────

internal sealed class BroadcastMpdResponse {
    [JsonPropertyName("success")]
    public int Success { get; init; }

    [JsonPropertyName("broadcastid")]
    public string BroadcastId { get; init; } = "0";

    [JsonPropertyName("viewertoken")]
    public string ViewerToken { get; init; } = "0";

    [JsonPropertyName("appid")]
    public int AppId { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    // Extra fields Steam returns — captured so JSON parsing doesn't fail
    [JsonPropertyName("gameid")]
    public string? GameId { get; init; }

    [JsonPropertyName("rtmp_url")]
    public string? RtmpUrl { get; init; }

    [JsonPropertyName("hls_url")]
    public string? HlsUrl { get; init; }
}

internal sealed class HeartbeatResponse {
    [JsonPropertyName("success")]
    public int Success { get; init; }

    [JsonPropertyName("viewertoken")]
    public string ViewerToken { get; init; } = "0";
}
