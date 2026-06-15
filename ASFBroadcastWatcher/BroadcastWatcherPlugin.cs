// ASFBroadcastWatcher - ASF plugin to watch Steam broadcasts for drop rewards
// Supports multiple bot accounts simultaneously via the BCAST command

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Net;
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
            // Step 1: Load the broadcast watch page so Steam sets our viewer cookies
            string watchUrl = $"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}";
            Uri watchUri = new(watchUrl);

            ObjectResponse<JsonElement>? watchResponse = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<JsonElement>(watchUri).ConfigureAwait(false);
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Watch page loaded.");

            // Step 2: Get the sessionid cookie — Steam needs this as a POST body param
            CookieCollection cookies = bot.ArchiWebHandler.WebBrowser.CookieContainer.GetCookies(new Uri("https://steamcommunity.com"));
            string sessionId = cookies["sessionid"]?.Value ?? "";
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: sessionid={(string.IsNullOrEmpty(sessionId) ? "(empty)" : "found")}");

            // Step 3: POST to getbroadcastmpd with steamid + sessionid
            Uri mpdUri = new("https://steamcommunity.com/broadcast/getbroadcastmpd/");
            Dictionary<string, string> mpdParams = new() {
                { "steamid", broadcasterSteamId },
                { "broadcastid", "0" },
                { "viewertoken", "0" },
                { "sessionid", sessionId },
            };

            ObjectResponse<BroadcastMpdResponse>? mpdResponse = await bot.ArchiWebHandler.WebBrowser.UrlPostToJsonObject<BroadcastMpdResponse, Dictionary<string, string>>(
                mpdUri,
                data: mpdParams,
                referer: watchUri
            ).ConfigureAwait(false);

            BroadcastMpdResponse? mpd = mpdResponse?.Content;
            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: getbroadcastmpd response: success={mpd?.Success} broadcastid={mpd?.BroadcastId}");

            if (mpd == null || mpd.Success == 0 || string.IsNullOrEmpty(mpd.BroadcastId) || mpd.BroadcastId == "0") {
                return $"❌ {bot.BotName}: Could not get broadcast session from Steam. Is the broadcast actually live?";
            }

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Got broadcastid={mpd.BroadcastId} viewertoken={mpd.ViewerToken}");

            WatchSession watchSession = new(bot, broadcasterSteamId, mpd.BroadcastId, mpd.ViewerToken);
            ActiveSessions[bot.BotName] = watchSession;
            watchSession.Start();

            return $"✅ {bot.BotName}: Now watching broadcast by {broadcasterSteamId}. Heartbeating every 20s. Use BCASTSTOP {bot.BotName} to stop.";
        } catch (Exception ex) {
            bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {bot.BotName}");
            return $"❌ {bot.BotName}: Exception — {ex.Message}";
        }
    }

    internal static async Task<BroadcastMpdResponse?> GetBroadcastMpdAsync(Bot bot, string broadcasterSteamId, string viewerToken, string broadcastId) {
        try {
            string watchUrl = $"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}";
            Uri watchUri = new(watchUrl);

            CookieCollection cookies = bot.ArchiWebHandler.WebBrowser.CookieContainer.GetCookies(new Uri("https://steamcommunity.com"));
            string sessionId = cookies["sessionid"]?.Value ?? "";

            Uri mpdUri = new("https://steamcommunity.com/broadcast/getbroadcastmpd/");
            Dictionary<string, string> mpdParams = new() {
                { "steamid", broadcasterSteamId },
                { "broadcastid", broadcastId },
                { "viewertoken", viewerToken },
                { "sessionid", sessionId },
            };

            ObjectResponse<BroadcastMpdResponse>? mpdResponse = await bot.ArchiWebHandler.WebBrowser.UrlPostToJsonObject<BroadcastMpdResponse, Dictionary<string, string>>(
                mpdUri,
                data: mpdParams,
                referer: watchUri
            ).ConfigureAwait(false);

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: getbroadcastmpd (re-register): success={mpdResponse?.Content?.Success}");
            return mpdResponse?.Content;
        } catch (Exception ex) {
            bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {bot.BotName} GetBroadcastMpdAsync");
            return null;
        }
    }

    internal static async Task<HeartbeatResponse?> SendHeartbeatAsync(Bot bot, string broadcasterSteamId, string sessionId, string token) {
        try {
            string heartbeatUrl =
                $"https://api.steampowered.com/ISteamBroadcast/ViewerHeartbeat/v1/" +
                $"?steamid={Uri.EscapeDataString(broadcasterSteamId)}" +
                $"&sessionid={Uri.EscapeDataString(sessionId)}" +
                $"&token={Uri.EscapeDataString(token)}" +
                $"&stream=0";

            Uri heartbeatUri = new(heartbeatUrl);
            Uri refererUri = new($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}");

            ObjectResponse<HeartbeatResponse>? hbResponse = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<HeartbeatResponse>(
                heartbeatUri,
                referer: refererUri
            ).ConfigureAwait(false);

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Heartbeat response: result={hbResponse?.Content?.Result}");
            return hbResponse?.Content;
        } catch (Exception ex) {
            bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {bot.BotName} SendHeartbeatAsync");
            return null;
        }
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
    private string _sessionId;
    private string _token;
    private readonly CancellationTokenSource _cts = new();

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
                    _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat failed (result={heartbeat?.Result}), re-registering...");

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
    [JsonPropertyName("result")]
    public int Result { get; init; }

    [JsonPropertyName("token")]
    public string Token { get; init; } = "0";
}
