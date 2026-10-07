# Configuration

[← Back to README](README.md) ｜ [中文](../configuration.md)

By default the program reads the `config.json` **next to the binary** (regardless of the directory you start it from; when invoked through a symlink, the real binary's directory is used), or use `--config <path>` for another location. If the file is missing it runs with defaults (notifications disabled) and prints a warning (including the path it looked at); on a configuration error it prints the specific errors and exits with code 1.

JSON comments and trailing commas are allowed; missing keys fall back to their defaults.

| Key | Type | Default | Description |
|---|---|---|---|
| `nickname` | string | `""` | Instance nickname; falls back to the system hostname when empty |
| `port` | int (1..65535) | `2222` | Listen port |
| `bind_family` | `"auto"` / `"ipv4"` / `"ipv6"` | `"auto"` | Listen address family; auto = dual stack |
| `delay_ms` | int (≥1) | `10000` | Delay between dripped lines (ms) |
| `line_length` | int (3..255) | `32` | Max random line content length (trailing CRLF excluded) |
| `max_clients` | int (≥1) | `4096` | Max concurrent connections; when full, accept pauses and new connections wait in the system backlog |
| `log_level` | `"error"` / `"info"` / `"debug"` | `"info"` | Log level (stdout; goes to journald under systemd) |
| `notifications.telegram.enabled` | bool | `false` | Telegram channel switch |
| `notifications.telegram.bot_token` | string | `""` | Telegram bot token (keep it secret) |
| `notifications.telegram.chat_id` | string | `""` | Target chat ID |
| `notifications.notify_on_connect` | bool | `true` | Connect notifications |
| `notifications.notify_on_close` | bool | `true` | Disconnect notifications |
| `notifications.dedup_per_ip_seconds` | int (≥0) | `300` | Per-IP cooldown in seconds; 0 = off |
| `notifications.max_per_minute` | int (≥1) | `3` | Global notifications per minute |

- With `notifications.telegram.enabled=true`, `bot_token` and `chat_id` must be non-empty, otherwise startup fails with exit code 1.
- Example file: `config.example.json` in the release archive (`config/config.example.json` in the repository). A minimal config only needs a nickname and the notification credentials:

```json
{
  "nickname": "home-vps",
  "notifications": {
    "telegram": { "enabled": true, "bot_token": "<your token>", "chat_id": "<your chat ID>" }
  }
}
```

> `config.json` holds your Telegram bot token (think of it as the bot's password): `chmod 600 config.json` is recommended so that only the user running the program can read it. A leaked token lets anyone send messages to your chat as this bot.

To make a running instance pick up changes, see [Command line & signals](cli.md) (`SIGHUP` hot-reloads the config).
