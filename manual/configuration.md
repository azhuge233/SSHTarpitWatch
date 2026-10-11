# 配置

[← 返回 README](../README.md) ｜ [English](en/configuration.md)

程序默认读取**二进制所在目录**的 `config.json`（与你在哪个目录启动无关；经符号链接调用时按真实二进制所在目录算），也可以用 `--config <路径>` 指定其他位置。文件不存在时按默认值运行（通知关闭）并打印警告（含它查找的路径）；配置有错时打印具体错误并以退出码 1 退出。

JSON 允许注释与尾逗号；缺省的键使用默认值。**键名拼错、或使用旧版键名不会被静默忽略**——启动时直接报错并逐条提示该改成什么（见下面「旧版配置迁移」）。

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `nickname` | string | `""` | 实例昵称；空则回退系统主机名 |
| `log_level` | `"error"` / `"info"` / `"debug"` | `"info"` | 日志级别（输出到 stdout；systemd 下进 journald） |
| `server.port` | int（1..65535） | `2222` | 监听端口 |
| `server.bind_family` | `"auto"` / `"ipv4"` / `"ipv6"` | `"auto"` | 监听地址族；auto = 双栈 |
| `server.delay_ms` | int（≥1） | `10000` | 慢滴行间隔（毫秒） |
| `server.line_length` | int（3..255） | `32` | 随机行内容长度上限（不含行尾 CRLF） |
| `server.max_clients` | int（≥1） | `4096` | 最大并发连接数；满员时暂停接受新连接（新连接滞留系统 backlog） |
| `notifications.telegram.enabled` | bool | `false` | Telegram 渠道开关 |
| `notifications.telegram.bot_token` | string | `""` | Telegram Bot Token（保密） |
| `notifications.telegram.chat_id` | string | `""` | 目标会话 ID |
| `notifications.filter.on_connect` | bool | `false` | 连接通知开关（默认只在断开时发一条） |
| `notifications.filter.on_close` | bool | `true` | 断开通知开关 |
| `notifications.filter.min_duration_seconds` | int（≥0） | `0` | 只通知停留**达到**该秒数的连接（0 = 不过滤） |
| `notifications.throttle.per_ip_seconds` | int（≥0） | `300` | 每 IP 冷却秒数；0 = 关闭 |
| `notifications.throttle.max_per_minute` | int（≥1） | `3` | 全局每分钟通知上限 |

- `notifications.telegram.enabled=true` 时 `bot_token` 与 `chat_id` 必须非空，否则启动报错退出（退出码 1）。
- `on_connect` 与 `on_close` 同时为 `false` 是允许的：程序照常困住连接、照常记日志与统计，只是不发通知；启动时会打印一条警告提醒你这个状态。
- 配置样例：发布包内为 `config.example.json`（仓库中为 `config/config.example.json`）。最小配置只需昵称与通知凭据，例如顺便只通知停留 60 秒以上的连接：

```json
{
  "nickname": "home-vps",
  "server": { "port": 2222 },
  "notifications": {
    "telegram": { "enabled": true, "bot_token": "<你的 Token>", "chat_id": "<你的 Chat ID>" },
    "filter": { "min_duration_seconds": 60 }
  }
}
```

## 旧版配置迁移

配置结构已重排：相关键收进 `server` / `notifications.filter` / `notifications.throttle` 三个段。**旧键名会直接报错**（不会静默失效），报错信息逐条给出新键名，例如：

```
port 已改为 server.port，请更新配置
notifications.dedup_per_ip_seconds 已改为 notifications.throttle.per_ip_seconds，请更新配置
```

对照表：

| 旧键 | 新键 |
|---|---|
| `port` / `bind_family` / `delay_ms` / `line_length` / `max_clients` | 加 `server.` 前缀（键名不变） |
| `notifications.notify_on_connect` / `notify_on_close` | `notifications.filter.on_connect` / `notifications.filter.on_close` |
| `notifications.dedup_per_ip_seconds` | `notifications.throttle.per_ip_seconds` |
| `notifications.max_per_minute` | `notifications.throttle.max_per_minute` |

> 同时还有一处默认行为变化：现在默认**只在连接断开时发一条通知**（合并了连接期的信息），不再单独发"新连接"消息；想要两条的话把 `notifications.filter.on_connect` 设为 `true`。详见[通知](notifications.md)。

> `config.json` 里是 Telegram Bot Token（相当于这个 bot 的密码）：建议 `chmod 600 config.json`，只有运行它的用户可读。token 一旦泄露，任何人都能以这个 bot 的名义往你的会话发消息。

改完配置让运行中的实例生效：见[命令行与信号](cli.md)（`SIGHUP` 热重载）。
