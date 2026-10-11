# Notifications

[← Back to README](README.md) ｜ [中文](../notifications.md)

Telegram is the only notification channel in v1 (other channels are reserved for a future extension interface, not implemented yet).

## What the notifications look like

By default you get **exactly one notification per connection, sent when it disconnects** — everything gathered while the client was trapped (its identification string, the local port, the connect time) is merged into that single message:

```
🕳 home-vps · SSHTarpitWatch · 连接断开
来源：203.0.113.7:54321 → 本机 :2222
客户端：SSH-2.0-libssh2_1.11.0
时间：2026-10-11 09:02:03 (UTC+08:00)
停留：0 小时 12 分 39 秒
发出：4096 字节
该 IP 今日第 3 次
```

- `时间` is the moment the connection was **established** (not when it dropped); `停留` is how long the client was trapped.
- The notification text is currently in Chinese.
- An empty `nickname` (`""`) shows the system hostname instead.
- If the client did not report an identification string, the client line shows `—`.
- Times are the server's local time (with UTC offset).

To also get a message the moment a connection arrives (two messages: connect + disconnect), set `notifications.filter.on_connect` to `true`:

```
🕳 home-vps · SSHTarpitWatch · 新连接
来源：203.0.113.7:54321 → 本机 :2222
客户端：SSH-2.0-libssh2_1.11.0
时间：2026-10-11 09:02:03 (UTC+08:00)
该 IP 今日第 3 次
```

Test notification (sent by `--test-notify`, deliberately distinct from real events):

```
🕳 home-vps · SSHTarpitWatch · 测试通知
时间：2026-10-11 09:02:03 (UTC+08:00)
```

## Only notifying connections that lasted long enough

`notifications.filter.min_duration_seconds` (default `0` = no filter): only connections lasting **at least** this many seconds are reported. Scanners that connect and immediately leave are ignored automatically; together with the default one-message-on-disconnect behaviour, your notification volume drops from "one per probe" to "one per real entanglement".

- The decision uses the config value **at disconnect time** — a reload (`SIGHUP`) applies immediately to connections already hanging.
- **Duration resolution ≈ the drip interval**: the program only notices that a client left when it next writes to it — every `server.delay_ms` (10 seconds by default). So the "time trapped" is measured to roughly that resolution: a scanner that connects and leaves in 0.1 s may be recorded as ~10 s. To reliably filter such probes, set the threshold clearly above `server.delay_ms` (60 s or more with the default settings); lowering `server.delay_ms` sharpens the measurement (at the cost of a faster drip and more bandwidth — the opposite of the tarpit's "trap as cheaply as possible" intent).
- A connection dropped by the duration filter does **not** consume that IP's cooldown budget and does not affect later connections from the same IP.
- It still **counts towards** "today's Nth connection from this IP".

## Deduplication and rate limiting

- **Per-IP cooldown** (`notifications.throttle.per_ip_seconds`, default 300 s): an IP is reported at most once per cooldown window; 0 disables it.
- **With connect notifications on** (`filter.on_connect=true`): the disconnect notice follows its connect notice — if a connection's connect notification was suppressed by the cooldown, its disconnect notice is suppressed too. With the default (connect notifications off), disconnect notices use the same per-IP cooldown independently.
- **Turning connect notifications on mid-flight** (flipping `filter.on_connect` from `false` to `true` via `SIGHUP`): connections **already hanging** never had a connect message, so by the follow rule their disconnect notices are not sent either (logged as `suppressed_dedup`); connections **established after** the switch behave normally with two messages.
- **Global rate limit** (`notifications.throttle.max_per_minute`, default 3): all notifications share one token bucket — average rate 3 per minute, burst up to 3.
- **Queue full = drop new**: notifications go through a bounded in-memory queue (1024 entries); when it is full, new entries are dropped and counted — the tarpit is never slowed down.
- Failed sends are retried (up to 3 times; backoff 2 s / 10 s / 30 s; a Telegram 429 honors its `retry_after`); a final failure is only logged.
- The cooldown window and the "today's Nth" counter live **in memory only**: restarting the process clears both (an IP may be reported once more, and the counter starts over).

Related config keys: see [Configuration](configuration.md).
