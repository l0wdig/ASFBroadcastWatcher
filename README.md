# ASFBroadcastWatcher

An ASF (ArchiSteamFarm) plugin that watches Steam broadcasts on multiple accounts simultaneously.

Designed for cases where game publishers give out free Steam items (backgrounds, emoticons, trading cards) as rewards for watching their broadcasts for a set duration.

---

## How to get the DLL (no .NET SDK needed)

1. Create a free account on [github.com](https://github.com) if you don't have one
2. Click the **"+"** button (top right) → **"New repository"**
3. Name it anything, e.g. `ASFBroadcastWatcher`, set it to **Public**, click **Create**
4. Upload all files from this zip into the repository (drag & drop works on the GitHub website)
5. After upload, go to the **Actions** tab → the build starts automatically
6. Wait ~1-2 minutes, then go to the **Releases** section (right sidebar)
7. Download `ASFBroadcastWatcher.zip` from the release

That's it — GitHub compiles it for free every time you push.

---

## Installation

1. Extract the downloaded `ASFBroadcastWatcher.zip` into your ASF `plugins/` folder

```
ASF/
└── plugins/
    └── ASFBroadcastWatcher/
        └── ASFBroadcastWatcher.dll
```

2. Restart ASF — you should see this in the log:
```
ASFBroadcastWatcher 1.0.0.0 loaded. Commands: BCAST <Bots> <BroadcastUrl> | BCASTSTOP <Bots> | BCASTLIST
```

---

## Commands

| Command | Access | Description |
|---|---|---|
| `BCAST <Bots> <URL>` | Master | Start watching a broadcast on the specified bot(s) |
| `BCASTSTOP <Bots>` | Master | Stop watching on the specified bot(s) |
| `BCASTLIST` | Operator | List all currently active watch sessions |

**Bot targeting** follows standard ASF syntax:
- Single bot: `BCAST myaccount https://...`
- Multiple bots: `BCAST account1,account2 https://...`
- All bots: `BCAST ASF https://...`

---

## Usage examples

```
# Watch on one account
!BCAST myaccount https://steamcommunity.com/broadcast/watch/76561198888084799

# Watch on multiple accounts
!BCAST account1,account2,account3 https://steamcommunity.com/broadcast/watch/76561198888084799

# Watch on all configured bots at once
!BCAST ASF https://steamcommunity.com/broadcast/watch/76561198888084799

# Check what's currently being watched and for how long
!BCASTLIST

# Stop watching on one account
!BCASTSTOP account1

# Stop watching on all accounts
!BCASTSTOP ASF
```

> **Note:** `!` is the default ASF CommandPrefix. Adjust if you changed it.

---

## How it works

1. Uses each bot's existing authenticated Steam session (same one ASF uses for card farming — no extra login needed)
2. Calls `steamcommunity.com/broadcast/getbroadcastmpd/` to register the bot as a viewer and get a `viewertoken` + `broadcastid`
3. Sends a **heartbeat POST** every 60 seconds to `steamcommunity.com/broadcast/heartbeat/` — this is what Steam uses to track watch time for drop eligibility
4. Auto-stops when the broadcast ends (detected via heartbeat response), or when you call `BCASTSTOP`

---

## Notes

- Sessions do **not** survive ASF restarts — re-run `!BCAST` after restarting
- If a bot isn't logged on when you run `!BCAST`, it reports an error for that bot and skips it
- If the broadcaster goes offline mid-watch, the plugin auto-detects it and cleans up
