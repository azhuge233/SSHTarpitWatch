# SSHTarpitWatch

[中文](README.md) | **English**

An SSH tarpit with connection notifications: keep scanners and brute-forcers stuck, and get notified the moment someone steps in.

> **About this repository**: the code and documentation are **entirely AI-generated** (a human sets the direction and signs off).

## What it does

It runs a fake SSH server (port configurable, default 2222):

- Anyone who tries to SSH into it is slowly fed random data — like [skeeto/endlessh](https://github.com/skeeto/endlessh), the handshake never completes and the connection hangs for hours or days, wasting the attacker's connections and patience. Your real SSH service stays on another port, unaffected.
- On every connection (and disconnection) it sends you a Telegram notification with:
  - source IP and port, and the probed local port
  - the client's **identification string** (e.g. `SSH-2.0-libssh2_1.11.0`) — you instantly see what tool it is
  - time, and the per-IP daily connection count
  - on disconnect: how long it was trapped and how many bytes were sent
- Configurable instance **nickname**, so you can tell machines apart when they share one notification channel.
- Built-in **deduplication and rate limiting** to avoid notification floods.

Reading the client identification string and sending notifications never affect the tarpit: notification failures (timeouts, rate limits, network errors) are only logged, and the tarpit keeps running.

## Quick start

The program is a single linux-x64 binary (about 6.5 MB, no .NET runtime required). The archive contains 2 files: `sshtarpitwatch` (the binary) and `config.example.json` (example config). The directory you unpack into becomes its run directory, with `config.json` next to the binary (see "Configuration"). Example (Debian 13):

```bash
# 1. Unpack (file name looks like sshtarpitwatch-<version>-linux-x64.tar.gz)
tar -xzf sshtarpitwatch-<version>-linux-x64.tar.gz
cd sshtarpitwatch-<version>-linux-x64

# 2. Copy the example config and fill in your Telegram bot token and chat ID
cp config.example.json config.json
$EDITOR config.json

# 3. Start it (listens on port 2222 by default; Ctrl+C to stop)
./sshtarpitwatch

# 4. Send a test notification to check your credentials and network
./sshtarpitwatch --test-notify

# 5. Start at boot / restart on failure: see "systemd service" below
```

> `--test-notify` exit codes: `0` = sent successfully; `1` = configuration error (including a disabled channel or missing credentials); `2` = sending failed.

## Configuration

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

## Notifications

Telegram is the only notification channel in v1 (other channels are reserved for a future extension interface, not implemented yet).

### What the notifications look like

Connect (with the client's self-reported identification string):

```
🕳 home-vps · SSHTarpitWatch · 新连接
来源：203.0.113.7:54321 → 本机 :2222
客户端：SSH-2.0-libssh2_1.11.0
时间：2026-10-06 09:02:03 (UTC+08:00)
该 IP 今日第 3 次
```

Disconnect:

```
🕳 home-vps · SSHTarpitWatch · 连接断开
来源：203.0.113.7:54321
停留：0 小时 4 分 39 秒
发出：96 字节
```

Test notification (sent by `--test-notify`, deliberately distinct from real events):

```
🕳 home-vps · SSHTarpitWatch · 测试通知
时间：2026-10-06 09:02:03 (UTC+08:00)
```

- The notification text is currently in Chinese.
- An empty `nickname` (`""`) shows the system hostname instead.
- If the client did not report an identification string, the client line shows `—`.
- Times are the server's local time (with UTC offset).

### Deduplication and rate limiting

- **Per-IP cooldown** (`dedup_per_ip_seconds`, default 300 s): an IP is reported at most once per cooldown window; 0 disables it.
- **The disconnect notice follows its connect notice**: if a connection's connect notification was suppressed by the cooldown, its disconnect notice is suppressed too (with `notify_on_connect=false`, disconnect notices use the same per-IP cooldown independently).
- **Global rate limit** (`max_per_minute`, default 3): all notifications share one token bucket — average rate 3 per minute, burst up to 3.
- **Queue full = drop new**: notifications go through a bounded in-memory queue (1024 entries); when it is full, new entries are dropped and counted — the tarpit is never slowed down.
- Failed sends are retried (up to 3 times; backoff 2 s / 10 s / 30 s; a Telegram 429 honors its `retry_after`); a final failure is only logged.
- The "today's Nth connection" counter in a notification is per server-local calendar day and includes suppressed connections.
- The cooldown window and the "today's Nth" counter live **in memory only**: restarting the process clears both (an IP may be reported once more, and the counter starts over).

## Command line

| Option | Behavior |
|---|---|
| `--config <path>` | Configuration file (default: the `config.json` next to the binary) |
| `--version` | Print `SSHTarpitWatch <version>` and exit |
| `--help` | Print usage and exit |
| `--test-notify` | Load and validate the config, send one test notification (bypassing dedup and rate limits), then exit |

- Exit codes: `0` = success; `1` = usage or configuration error; `2` = test notification failed.
- No options = start the tarpit server.

## Signals

| Signal | Behavior |
|---|---|
| `SIGTERM` / `SIGINT` | Graceful shutdown: stop accepting connections → close existing connections → drain the notification queue (up to 5 s) → print TOTALS → exit 0 |
| `SIGHUP` | Reload the config; if the new config is invalid or the file is missing, the old config is kept and the process keeps running |
| `SIGUSR1` | Print TOTALS |

> Under systemd, trigger a reload with: `sudo systemctl kill -s HUP sshtarpitwatch`.

## systemd service (optional)

To have systemd manage SSHTarpitWatch (start at boot, restart on failure), you write the service file yourself. Its paths and run identity depend on your setup, so what follows is a **template** rather than a ready-made file; two spots need your values: `ExecStart` (absolute path to the binary) and `User`/`Group` (the account it runs as).

```ini
[Unit]
Description=SSHTarpitWatch — SSH tarpit with connection notifications
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
# absolute path to the binary (config.json sits next to it, so WorkingDirectory is not needed)
ExecStart=/opt/sshtarpitwatch/sshtarpitwatch
Restart=on-failure
RestartSec=5

# the account it runs as (your own username is fine; root is not needed)
User=youruser
Group=youruser

NoNewPrivileges=true
ProtectSystem=strict
PrivateTmp=true
# if the binary lives under /home or /root, remove the next line — ProtectHome hides home directories from the service
#ProtectHome=true
# enable the next line only for ports below 1024 (not needed for the default 2222)
#AmbientCapabilities=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
```

```bash
# 1. Write the service file (the template above, with the two spots filled in for your setup)
sudo $EDITOR /etc/systemd/system/sshtarpitwatch.service

# 2. Enable and start it
sudo systemctl daemon-reload
sudo systemctl enable --now sshtarpitwatch

# 3. Follow the logs (Ctrl+C to exit)
journalctl -u sshtarpitwatch -f
```

- **Config file permissions**: `chmod 600 config.json` so that only the user running the service can read it.
- **After editing the config**: hot-reload with `sudo systemctl kill -s HUP sshtarpitwatch` (an invalid config is rejected and the old one keeps running).
- **Ports below 1024** (e.g. 22; not needed for the default 2222): uncomment `AmbientCapabilities=CAP_NET_BIND_SERVICE`, or use a port ≥1024.
- **Uninstall**: `sudo systemctl disable --now sshtarpitwatch`, remove `/etc/systemd/system/sshtarpitwatch.service`, then delete the unpacked directory (including `config.json`). The program leaves no files anywhere else on the system.

## Requirements and deployment notes

- **Release form**: single linux-x64 native AOT binary (about 6.5 MB) — no .NET runtime required, no ICU needed.
- **System requirements**: glibc ≥ 2.34 (Debian 12 / Ubuntu 22.04 or newer); system libssl required (shipped with Debian by default).
- **Port**: default 2222; ports below 1024 need extra privilege (see the previous section).

## What client information is captured?

The SSH protocol requires clients to send their identification string (e.g. `SSH-2.0-OpenSSH_9.6p1`) immediately after connecting. This tool forwards it in the notification, so you can tell real SSH clients, scanners, and botnets apart at a glance.

## Credits

- Mechanism modeled after **[skeeto/endlessh](https://github.com/skeeto/endlessh)** (public domain). This project is an **independent implementation** in C# and contains none of its code.
- Inspired by **[shizunge/endlessh-go](https://github.com/shizunge/endlessh-go)**, a modern Go implementation of endlessh.
