# Configuration

[← Back to README](README.md) ｜ [中文](../configuration.md)

By default the program reads the `config.json` **next to the binary** (regardless of the directory you start it from; when invoked through a symlink, the real binary's directory is used), or use `--config <path>` for another location. If the file is missing it runs with defaults (notifications disabled) and prints a warning (including the path it looked at); on a configuration error it prints the specific errors and exits with code 1.

JSON comments and trailing commas are allowed; missing keys fall back to their defaults. **Misspelled keys, or keys from an older layout, are never silently ignored** — startup fails with a per-key message telling you what to rename (see "Migrating an older config" below).

| Key | Type | Default | Description |
|---|---|---|---|
| `nickname` | string | `""` | Instance nickname; falls back to the system hostname when empty |
| `log_level` | `"error"` / `"info"` / `"debug"` | `"info"` | Log level (stdout; goes to journald under systemd) |
| `server.port` | int (1..65535) | `2222` | Listen port |
| `server.bind_family` | `"auto"` / `"ipv4"` / `"ipv6"` | `"auto"` | Listen address family; auto = dual stack |
| `server.delay_ms` | int (≥1) | `10000` | Delay between dripped lines (ms) |
| `server.line_length` | int (3..255) | `32` | Max random line content length (trailing CRLF excluded) |
| `server.max_clients` | int (≥1) | `4096` | Max concurrent connections; when full, accept pauses and new connections wait in the system backlog |
| `notifications.telegram.enabled` | bool | `false` | Telegram channel switch |
| `notifications.telegram.bot_token` | string | `""` | Telegram bot token (keep it secret) |
| `notifications.telegram.chat_id` | string | `""` | Target chat ID |
| `notifications.filter.on_connect` | bool | `false` | Connect notification switch (by default only a disconnect notice is sent) |
| `notifications.filter.on_close` | bool | `true` | Disconnect notification switch |
| `notifications.filter.min_duration_seconds` | int (≥0) | `0` | Only notify connections lasting **at least** this many seconds (0 = no filter) |
| `notifications.throttle.per_ip_seconds` | int (≥0) | `300` | Per-IP cooldown in seconds; 0 = off |
| `notifications.throttle.max_per_minute` | int (≥1) | `3` | Global notifications per minute |

- With `notifications.telegram.enabled=true`, `bot_token` and `chat_id` must be non-empty, otherwise startup fails with exit code 1.
- Setting both `on_connect` and `on_close` to `false` is allowed: the tarpit keeps trapping, logging and counting as usual — it simply sends no notifications; startup prints a warning reminding you of that state.
- Example file: `config.example.json` in the release archive (`config/config.example.json` in the repository). A minimal config only needs a nickname and the notification credentials — here also only notifying connections that lasted 60 seconds or more:

```json
{
  "nickname": "home-vps",
  "server": { "port": 2222 },
  "notifications": {
    "telegram": { "enabled": true, "bot_token": "<your token>", "chat_id": "<your chat ID>" },
    "filter": { "min_duration_seconds": 60 }
  }
}
```

## Migrating an older config

The config layout has been reorganised: related keys now live under the `server`, `notifications.filter` and `notifications.throttle` sections. **Old key names make startup fail** (never a silent no-op), with a per-key message naming the new key — the messages themselves are currently in Chinese:

```
port 已改为 server.port，请更新配置
notifications.dedup_per_ip_seconds 已改为 notifications.throttle.per_ip_seconds，请更新配置
```

Mapping table:

| Old key | New key |
|---|---|
| `port` / `bind_family` / `delay_ms` / `line_length` / `max_clients` | add the `server.` prefix (same names) |
| `notifications.notify_on_connect` / `notify_on_close` | `notifications.filter.on_connect` / `notifications.filter.on_close` |
| `notifications.dedup_per_ip_seconds` | `notifications.throttle.per_ip_seconds` |
| `notifications.max_per_minute` | `notifications.throttle.max_per_minute` |

> One behaviour change comes with it: by default you now get **one notification per connection, sent when it disconnects** (with the connect-time information merged in); there is no separate "new connection" message unless you set `notifications.filter.on_connect` to `true`. See [Notifications](notifications.md).

> `config.json` holds your Telegram bot token (think of it as the bot's password): `chmod 600 config.json` is recommended so that only the user running the program can read it. A leaked token lets anyone send messages to your chat as this bot.

To make a running instance pick up changes, see [Command line & signals](cli.md) (`SIGHUP` hot-reloads the config).
