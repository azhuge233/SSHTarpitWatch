# 配置

[← 返回 README](../README.md) ｜ [English](en/configuration.md)

程序默认读取**二进制所在目录**的 `config.json`（与你在哪个目录启动无关；经符号链接调用时按真实二进制所在目录算），也可以用 `--config <路径>` 指定其他位置。文件不存在时按默认值运行（通知关闭）并打印警告（含它查找的路径）；配置有错时打印具体错误并以退出码 1 退出。

JSON 允许注释与尾逗号；缺省的键使用默认值。

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `nickname` | string | `""` | 实例昵称；空则回退系统主机名 |
| `port` | int（1..65535） | `2222` | 监听端口 |
| `bind_family` | `"auto"` / `"ipv4"` / `"ipv6"` | `"auto"` | 监听地址族；auto = 双栈 |
| `delay_ms` | int（≥1） | `10000` | 慢滴行间隔（毫秒） |
| `line_length` | int（3..255） | `32` | 随机行内容长度上限（不含行尾 CRLF） |
| `max_clients` | int（≥1） | `4096` | 最大并发连接数；满员时暂停接受新连接（新连接滞留系统 backlog） |
| `log_level` | `"error"` / `"info"` / `"debug"` | `"info"` | 日志级别（输出到 stdout；systemd 下进 journald） |
| `notifications.telegram.enabled` | bool | `false` | Telegram 渠道开关 |
| `notifications.telegram.bot_token` | string | `""` | Telegram Bot Token（保密） |
| `notifications.telegram.chat_id` | string | `""` | 目标会话 ID |
| `notifications.notify_on_connect` | bool | `true` | 连接通知开关 |
| `notifications.notify_on_close` | bool | `true` | 断开通知开关 |
| `notifications.dedup_per_ip_seconds` | int（≥0） | `300` | 每 IP 冷却秒数；0 = 关闭 |
| `notifications.max_per_minute` | int（≥1） | `3` | 全局每分钟通知上限 |

- `notifications.telegram.enabled=true` 时 `bot_token` 与 `chat_id` 必须非空，否则启动报错退出（退出码 1）。
- 配置样例：发布包内为 `config.example.json`（仓库中为 `config/config.example.json`）。最小配置只需昵称与通知凭据：

```json
{
  "nickname": "home-vps",
  "notifications": {
    "telegram": { "enabled": true, "bot_token": "<你的 Token>", "chat_id": "<你的 Chat ID>" }
  }
}
```

> `config.json` 里是 Telegram Bot Token（相当于这个 bot 的密码）：建议 `chmod 600 config.json`，只有运行它的用户可读。token 一旦泄露，任何人都能以这个 bot 的名义往你的会话发消息。

改完配置让运行中的实例生效：见[命令行与信号](cli.md)（`SIGHUP` 热重载）。
