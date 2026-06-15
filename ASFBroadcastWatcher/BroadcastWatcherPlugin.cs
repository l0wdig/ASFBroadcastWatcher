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

    internal static readonly ConcurrentDictionary<string, WatchSession> ActiveSessions = new(StringComparer.OrdinalIgnoreCase);

    public Task OnLoaded() {
        ASF.ArchiLogger.LogGenericInfo($"{Name} {Version} loaded.");
        return Task.CompletedTask;
    }

    public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
        if (args.Length == 0) return null;

        switch (args[0].ToUpperInvariant()) {
            case "BCAST" when access >= EAccess.Master: {
                if (args.Length < 3)
                    return "Usage: BCAST <BotNames|ASF> <BroadcastUrl>";

                string? broadcasterSteamId = ExtractSteamIdFromUrl(args[2]);
                if (broadcasterSteamId == null)
                    return $"❌ Could not parse SteamID64 from URL: {args[2]}";

                HashSet<Bot>? bots = Bot.GetBots(args[1]);
                if (bots == null || bots.Count == 0)
                    return $"❌ No bots found matching: {args[1]}";

                string[] results = await Task.WhenAll(bots.Select(b => StartWatchingAsync(b, broadcasterSteamId))).ConfigureAwait(false);
                return string.Join("\n", results);
            }

            case "BCASTSTOP" when access >= EAccess.Master: {
                if (args.Length < 2) return "Usage: BCASTSTOP <BotNames|ASF>";
                HashSet<Bot>? bots = Bot.GetBots(args[1]);
                if (bots == null || bots.Count == 0) return $"❌ No bots found: {args[1]}";
                List<string> results = new();
                foreach (Bot b in bots) {
                    if (ActiveSessions.TryRemove(b.BotName, out WatchSession? s)) {
                        await s.StopAsync().ConfigureAwait(false);
                        results.Add($"✅ {b.BotName}: Stopped.");
                    } else {
                        results.Add($"ℹ️ {b.BotName}: No active session.");
                    }
                }
                return string.Join("\n", results);
            }

            case "BCASTLIST" when access >= EAccess.Operator: {
                if (ActiveSessions.IsEmpty) return "ℹ️ No active sessions.";
                return "📺 Active:\n" + string.Join("\n", ActiveSessions.Select(kv =>
                    $"  • {kv.Key} → {kv.Value.BroadcasterSteamId} | {(int)(DateTime.UtcNow - kv.Value.StartedAt).TotalMinutes}m elapsed"));
            }

            default: return null;
        }
    }

    private static async Task<string> StartWatchingAsync(Bot bot, string broadcasterSteamId) {
        if (!bot.IsConnectedAndLoggedOn)
            return $"❌ {bot.BotName}: Not logged on.";

        if (ActiveSessions.TryRemove(bot.BotName, out WatchSession? existing))
            await existing.StopAsync().ConfigureAwait(false);

        try {
            Uri watchUri = new($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}");
            await bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(watchUri).ConfigureAwait(false);

            BroadcastMpdResponse? mpd = await GetBroadcastMpdRawAsync(bot, broadcasterSteamId).ConfigureAwait(false);

            if (mpd == null) {
                return $"❌ {bot.BotName}: getbroadcastmpd returned null. Check bot session.";
            }

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: mpd success={mpd.SuccessRaw} broadcastid={mpd.BroadcastId} viewertoken={mpd.ViewerToken}");

            if (mpd.IsUnavailable) {
                return $"❌ {bot.BotName}: Broadcast not available (success={mpd.SuccessRaw}). Is {broadcasterSteamId} live?";
            }

            WatchSession session = new(bot, broadcasterSteamId, mpd.BroadcastId, mpd.ViewerToken);
            ActiveSessions[bot.BotName] = session;
            session.Start();

            return $"✅ {bot.BotName}: Watching {broadcasterSteamId} (mpd success={mpd.SuccessRaw}). Use BCASTSTOP {bot.BotName} to stop.";
        } catch (Exception ex) {
            bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {bot.BotName}: StartWatchingAsync failed");
            return $"❌ {bot.BotName}: Exception — {ex.Message}";
        }
    }

    internal static async Task<BroadcastMpdResponse?> GetBroadcastMpdRawAsync(Bot bot, string broadcasterSteamId, string broadcastId = "0", string viewerToken = "0") {
        Uri mpdUri = new("https://steamcommunity.com/broadcast/getbroadcastmpd/");
        Uri referer = new($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}");

        Dictionary<string, string> data = new() {
            { "steamid", broadcasterSteamId },
            { "broadcastid", broadcastId },
            { "viewertoken", viewerToken },
        };

        try {
            ObjectResponse<BroadcastMpdResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<BroadcastMpdResponse>(
                mpdUri,
                data: data,
                referer: referer,
                requestOptions: ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnClientErrors | ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnServerErrors
            ).ConfigureAwait(false);

            if (response?.Content != null) {
                bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: getbroadcastmpd response: success={response.Content.SuccessRaw} broadcastid={response.Content.BroadcastId} token={response.Content.ViewerToken}");
                return response.Content;
            }

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: getbroadcastmpd response was null");
            return null;
        } catch (Exception ex) {
            bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {bot.BotName}: getbroadcastmpd threw exception");
            return null;
        }
    }

    internal static async Task<HeartbeatResponse?> SendHeartbeatAsync(Bot bot, string broadcasterSteamId, string broadcastId, string viewerToken) {
        Uri heartbeatUri = new("https://steamcommunity.com/broadcast/heartbeat/");
        Uri referer = new($"https://steamcommunity.com/broadcast/watch/{broadcasterSteamId}");

        Dictionary<string, string> data = new() {
            { "steamid", broadcasterSteamId },
            { "broadcastid", broadcastId },
            { "viewertoken", viewerToken },
        };

        try {
            ObjectResponse<HeartbeatResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<HeartbeatResponse>(
                heartbeatUri,
                data: data,
                referer: referer,
                requestOptions: ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnClientErrors | ArchiSteamFarm.Web.WebBrowser.ERequestOptions.ReturnServerErrors
            ).ConfigureAwait(false);

            if (response?.Content != null) {
                bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Heartbeat success={response.Content.SuccessRaw} token={response.Content.ViewerToken}");
                return response.Content;
            }

            bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {bot.BotName}: Heartbeat response null");
            return null;
        } catch (Exception ex) {
            bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {bot.BotName}: Heartbeat threw exception");
            return null;
        }
    }

    private static async Task<string?> GetSessionIdAsync(Bot bot) {
        try {
            Uri testUri = new("https://steamcommunity.com/broadcast/watch/");
            await bot.ArchiWebHandler.UrlGetToHtmlDocumentWithSession(testUri).ConfigureAwait(false);
        } catch { /* ignore */ }
        return null;
    }

    private static string? ExtractSteamIdFromUrl(string url) {
        int idx = url.IndexOf("/broadcast/watch/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) {
            string remainder = url[(idx + "/broadcast/watch/".Length)..].TrimEnd('/').Split('?')[0];
            if (remainder.Length > 0 && remainder.All(char.IsDigit))
                return remainder;
        }
        return null;
    }
}

internal sealed class WatchSession {
    internal readonly string BroadcasterSteamId;
    internal readonly DateTime StartedAt;

    private readonly Bot _bot;
    private string _broadcastId;
    private string _viewerToken;
    private readonly CancellationTokenSource _cts = new();
    private const int HeartbeatIntervalSeconds = 55;
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
        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Loop started for {BroadcasterSteamId}. broadcastid={_broadcastId} token={_viewerToken}");
        int failures = 0;

        // Stagger start: each bot waits a random 0-30s before first heartbeat
        // so 50 bots don't all hit Steam at the exact same time
        int staggerSeconds = Random.Shared.Next(0, 30);
        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Stagger delay {staggerSeconds}s before first heartbeat.");
        await Task.Delay(TimeSpan.FromSeconds(staggerSeconds), ct).ConfigureAwait(false);

        try {
            while (!ct.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                if (_broadcastId == "0" || string.IsNullOrEmpty(_broadcastId)) {
                    _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: No broadcastid yet, retrying getbroadcastmpd...");
                    BroadcastMpdResponse? mpd = await BroadcastWatcherPlugin.GetBroadcastMpdRawAsync(_bot, BroadcasterSteamId).ConfigureAwait(false);
                    if (mpd != null && mpd.IsReady && mpd.BroadcastId != "0") {
                        _broadcastId = mpd.BroadcastId;
                        _viewerToken = mpd.ViewerToken;
                        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Got broadcastid={_broadcastId}");
                    } else {
                        failures++;
                        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Still no broadcastid (failure {failures}/{MaxConsecutiveFailures})");
                        if (failures >= MaxConsecutiveFailures) {
                            _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Giving up after {failures} failures.");
                            BroadcastWatcherPlugin.ActiveSessions.TryRemove(_bot.BotName, out _);
                            break;
                        }
                        continue;
                    }
                }

                HeartbeatResponse? hb = await BroadcastWatcherPlugin.SendHeartbeatAsync(_bot, BroadcasterSteamId, _broadcastId, _viewerToken).ConfigureAwait(false);

                if (hb == null || !hb.IsSuccess) {
                    failures++;
                    _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat failed (attempt {failures}/{MaxConsecutiveFailures}), success={hb?.SuccessRaw}");

                    if (failures >= MaxConsecutiveFailures) {
                        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Attempting full reconnect...");
                        BroadcastMpdResponse? mpd = await BroadcastWatcherPlugin.GetBroadcastMpdRawAsync(_bot, BroadcasterSteamId, _broadcastId, _viewerToken).ConfigureAwait(false);
                        if (mpd != null && mpd.IsReady) {
                            _broadcastId = mpd.BroadcastId;
                            _viewerToken = mpd.ViewerToken;
                            failures = 0;
                            _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Reconnected!");
                        } else {
                            _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Broadcast by {BroadcasterSteamId} ended or unavailable. Stopping.");
                            BroadcastWatcherPlugin.ActiveSessions.TryRemove(_bot.BotName, out _);
                            break;
                        }
                    }
                    continue;
                }

                failures = 0;
                if (!string.IsNullOrEmpty(hb.ViewerToken) && hb.ViewerToken != "0")
                    _viewerToken = hb.ViewerToken;

                _bot.ArchiLogger.LogGenericDebug($"[BroadcastWatcher] {_bot.BotName}: Heartbeat OK.");
            }
        } catch (OperationCanceledException) {
            // normal stop
        } catch (Exception ex) {
            _bot.ArchiLogger.LogGenericException(ex, $"[BroadcastWatcher] {_bot.BotName}: Loop error");
            BroadcastWatcherPlugin.ActiveSessions.TryRemove(_bot.BotName, out _);
        }

        _bot.ArchiLogger.LogGenericInfo($"[BroadcastWatcher] {_bot.BotName}: Heartbeat loop stopped.");
    }
}

internal sealed class BroadcastMpdResponse {
    // Steam sends success as a number OR a string like "ready" — use JsonElement to accept anything
    [JsonPropertyName("success")]
    public JsonElement Success { get; init; }

    [JsonPropertyName("broadcastid")]
    public string BroadcastId { get; init; } = "0";

    [JsonPropertyName("viewertoken")]
    public string ViewerToken { get; init; } = "0";

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("errmsg")]
    public string? ErrMsg { get; init; }

    // Raw value as string for logging
    public string SuccessRaw => Success.ValueKind switch {
        JsonValueKind.String => Success.GetString() ?? "",
        JsonValueKind.Number => Success.GetRawText(),
        _ => Success.GetRawText()
    };

    // "ready" or 1 both mean the broadcast is live and ready
    public bool IsReady => SuccessRaw is "ready" or "1" or "2";

    // 0 or "unavailable" etc. means not available
    public bool IsUnavailable => SuccessRaw is "0" or "" or "unavailable";
}

internal sealed class HeartbeatResponse {
    [JsonPropertyName("success")]
    public JsonElement Success { get; init; }

    [JsonPropertyName("viewertoken")]
    public string ViewerToken { get; init; } = "0";

    public string SuccessRaw => Success.ValueKind switch {
        JsonValueKind.String => Success.GetString() ?? "",
        JsonValueKind.Number => Success.GetRawText(),
        _ => Success.GetRawText()
    };

    public bool IsSuccess => SuccessRaw is "1" or "ready";
}
