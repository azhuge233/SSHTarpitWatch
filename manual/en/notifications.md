# Notifications

[← Back to README](README.md) ｜ [中文](../notifications.md)

Telegram is the only notification channel in v1 (other channels are reserved for a future extension interface, not implemented yet).

## What the notifications look like

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

## Deduplication and rate limiting

- **Per-IP cooldown** (`dedup_per_ip_seconds`, default 300 s): an IP is reported at most once per cooldown window; 0 disables it.
- **The disconnect notice follows its connect notice**: if a connection's connect notification was suppressed by the cooldown, its disconnect notice is suppressed too (with `notify_on_connect=false`, disconnect notices use the same per-IP cooldown independently).
- **Global rate limit** (`max_per_minute`, default 3): all notifications share one token bucket — average rate 3 per minute, burst up to 3.
- **Queue full = drop new**: notifications go through a bounded in-memory queue (1024 entries); when it is full, new entries are dropped and counted — the tarpit is never slowed down.
- Failed sends are retried (up to 3 times; backoff 2 s / 10 s / 30 s; a Telegram 429 honors its `retry_after`); a final failure is only logged.
- The "today's Nth connection" counter in a notification is per server-local calendar day and includes suppressed connections.
- The cooldown window and the "today's Nth" counter live **in memory only**: restarting the process clears both (an IP may be reported once more, and the counter starts over).

Related config keys: see [Configuration](configuration.md).
