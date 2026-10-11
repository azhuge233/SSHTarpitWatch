# 配置

[← 返回 README](../README.md) ｜ [English](en/configuration.md)

程序默认读取**二进制所在目录**的 `config.json`（与你在哪个目录启动无关；经符号链接调用时按真实二进制所在目录算），也可以用 `--config <路径>` 指定其他位置。文件不存在时按默认值运行（通知关闭）并打印警告（含它查找的路径）；配置有错时打印具体错误并以退出码 1 退出。

JSON 允许注释与尾逗号；缺省的键使用默认值。**键名拼错不会被静默忽略**——启动时直接报错并点名该键。

`config.json` 里是 Telegram Bot Token（相当于这个 bot 的密码）：建议 `chmod 600 config.json`，只有运行它的用户可读。token 一旦泄露，任何人都能以这个 bot 的名义往你的会话发消息。

改完配置让运行中的实例生效：见[命令行与信号](cli.md)（`SIGHUP` 热重载）。

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
| `notifications.geoip.enabled` | bool | `false` | IP 地区解析开关（离线库，见下文「IP 地区解析」） |
| `notifications.geoip.databases` | string[] | `[]` | 库文件路径列表（顺序 = 优先级；相对路径以**配置文件所在目录**为基准） |
| `notifications.geoip.language` | string | `"zh"` | 地区名语言；常用值 `zh` / `en` / `ja` / `ru` / `es` / `pt` / `de` / `fr` / `ko` / `fa`（库缺该语言时回退英文） |

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

## IP 地区解析（可选）

开启后，通知里会多出一行地区（库提供运营商时再多一行）：

```
地区：中国 · 江苏省 · 南京市 (CN)
ISP：电信 (AS4134)
```

- **库文件由你自备**，程序不联网取库。把库文件放在 `config.json` 旁边（相对路径即以此目录为基准），在 `notifications.geoip.databases` 里按顺序列出——**顺序 = 优先级**，逐项取第一个给得出的库：
  - **MMDB**（建议先试 [DB-IP Lite](https://db-ip.com/db/lite/)，免注册、CC BY 4.0）：GeoLite2 / DB-IP / IPinfo Lite 都能直接用；要省市粒度用 City 版（文件大些）；**GeoLite2-ASN** 只给 ASN（与地区库并用时，组织名与 AS 号会分别取各自能给的库）
  - **ip2region**（[官方库](https://github.com/lionsoul2014/ip2region)，Apache-2.0）：中国 IP 的省/市与运营商更细；v4、v6 各一个文件，都列进列表即可
- **国家名的语言**取决于哪个库先给出：想让中国 IP 显示「中国」（而不是 China），把 ip2region 排在 MMDB 之前。
- **换库生效**：替换文件后 `SIGHUP` 重载即可（程序按文件 mtime 自动重开）。
- **降级表现**：`enabled=true` 但库文件读不到 → 启动打印警告，通知显示 `地区：库缺失`；库正常但该 IP 无记录 → `地区：无查询结果`；`enabled=false` → 通知里不出现地区行。
- 这些离线库各有自己的许可与署名要求（下载页会写明）——启用前请到各自官网确认并遵守；本项目不分发任何库文件。

