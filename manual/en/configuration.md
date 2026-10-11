# Configuration

[← Back to README](README.md) ｜ [中文](../configuration.md)

By default the program reads the `config.json` **next to the binary** (regardless of the directory you start it from; when invoked through a symlink, the real binary's directory is used), or use `--config <path>` for another location. If the file is missing it runs with defaults (notifications disabled) and prints a warning (including the path it looked at); on a configuration error it prints the specific errors and exits with code 1.

JSON comments and trailing commas are allowed; missing keys fall back to their defaults. **Misspelled keys are never silently ignored** — startup fails and names the offending key.

`config.json` holds your Telegram bot token (think of it as the bot's password): `chmod 600 config.json` is recommended so that only the user running the program can read it. A leaked token lets anyone send messages to your chat as this bot.

To make a running instance pick up changes, see [Command line & signals](cli.md) (`SIGHUP` hot-reloads the config).

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
| `notifications.geoip.enabled` | bool | `false` | IP geolocation switch (offline database, see "IP geolocation" below) |
| `notifications.geoip.databases` | string[] | `[]` | Database file paths (order = priority; relative paths resolve against the **config file's directory**) |
| `notifications.geoip.language` | string | `"zh"` | Language for names; common values `zh` / `en` / `ja` / `ru` / `es` / `pt` / `de` / `fr` / `ko` / `fa` (falls back to English when the database lacks it) |

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

## IP geolocation (optional)

With it enabled, notifications gain a region line (and an ISP line when a database provides one):

```
地区：中国 · 江苏省 · 南京市 (CN)
ISP：电信 (AS4134)
```

- **You provide the database files** — the program never downloads them. Put them next to `config.json` (relative paths resolve against that directory) and list them in `notifications.geoip.databases`; **order = priority**, and each field is taken from the first database that has it:
  - **MMDB** ([DB-IP Lite](https://db-ip.com/db/lite/) is a good starting point — no sign-up, CC BY 4.0): GeoLite2 / DB-IP / IPinfo Lite all work as-is; use a City edition for city-level detail; **GeoLite2-ASN** supplies the ASN only (the operator name and the AS number may come from different databases when you list several)
  - **ip2region** ([official](https://github.com/lionsoul2014/ip2region), Apache-2.0): finer Chinese province/city and ISP data; one file for v4 and one for v6.
- **The language of names depends on which database answers first**: to get 中国 rather than China, list ip2region before the MMDB.
- **Swapping a database**: replace the file and send `SIGHUP` — the program reopens it when the file's modification time changes.
- **Degradation**: `enabled=true` with an unreadable database → a startup warning plus `地区：库缺失`; a readable database with no record for the address → `地区：无查询结果`; `enabled=false` → the region line never appears.
- These offline databases come with their own licence and attribution terms (stated on their download pages) — check and follow them before enabling; this project distributes none of them.

