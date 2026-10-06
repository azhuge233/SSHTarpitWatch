# SSHTarpitWatch

SSH 焦油坑 + 连接通知：把扫描器和暴力破解者吊住，并把踩坑消息第一时间推送到你的手机上。

## 这是做什么的

在公网上挂一个“假的 SSH 服务器”（端口可配，默认 2222）：

- 任何人尝试 SSH 登录它，都会像连上 [skeeto/endlessh](https://github.com/skeeto/endlessh) 那样被**慢慢喂随机内容**——握手永远不会完成，连接被吊住数小时甚至数天，白白消耗攻击者的连接与耐心；你的真实 SSH 服务放在别的端口，不受影响。
- 每次有客户端连接（以及断开），本工具会通过 Telegram 发一条通知，包含：
  - 来源 IP 与端口、被探测的本机端口
  - **客户端的身份串**（如 `SSH-2.0-libssh2_1.11.0`，一眼看出对方是什么工具）
  - 时间、该 IP 当日的第几次连接
  - 断开时还会告诉你“被吊了多久、发出了多少字节”
- 支持配置**实例昵称**：多台机器共用一个通知渠道时，一眼分辨消息来自哪台。
- 内置**去重与限速**：同一 IP 冷却期内只报一次、全局每分钟限流，防刷屏。

读客户端身份串、发通知都不会影响困住行为：通知链路故障（超时、被限流、网络错误）只会记日志，焦油坑照常运行。

## 快速开始

SSHTarpitWatch 是 linux-x64 单文件程序（约 6.5 MB，无需 .NET 运行时）；包内含 2 件：`sshtarpitwatch`（二进制）、`config.example.json`（配置样例）。解压出来的目录就是它的运行目录——`config.json` 与二进制放在一起（见“配置”）。以 Debian 13 为例：

```bash
# 1. 解压（文件名形如 sshtarpitwatch-<版本>-linux-x64.tar.gz）
tar -xzf sshtarpitwatch-<版本>-linux-x64.tar.gz
cd sshtarpitwatch-<版本>-linux-x64

# 2. 复制配置样例，填入你的 Telegram Bot Token 与 Chat ID
cp config.example.json config.json
$EDITOR config.json

# 3. 启动（默认监听 2222 端口；Ctrl+C 退出）
./sshtarpitwatch

# 4. 发一条测试通知，确认凭据与网络都没问题
./sshtarpitwatch --test-notify

# 5. 开机自启 / 崩溃重启：见下文“systemd 服务”
```

> `--test-notify` 退出码：`0` = 发送成功；`1` = 配置错误（含渠道未启用、凭据缺失）；`2` = 发送失败。

## 配置

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

## 通知

v1 的通知渠道为 Telegram（其他渠道仅预留扩展接口，未实现）。

### 通知长什么样

连接（含客户端自报的身份串）：

```
🕳 home-vps · SSHTarpitWatch · 新连接
来源：203.0.113.7:54321 → 本机 :2222
客户端：SSH-2.0-libssh2_1.11.0
时间：2026-10-06 09:02:03 (UTC+08:00)
该 IP 今日第 3 次
```

断开：

```
🕳 home-vps · SSHTarpitWatch · 连接断开
来源：203.0.113.7:54321
停留：0 小时 4 分 39 秒
发出：96 字节
```

测试通知（`--test-notify` 发出，与真实事件区分）：

```
🕳 home-vps · SSHTarpitWatch · 测试通知
时间：2026-10-06 09:02:03 (UTC+08:00)
```

- 昵称留空（`nickname: ""`）时显示系统主机名。
- 客户端未上报身份串时，`客户端` 一行显示 `—`。
- 时间为服务器本地时间（含 UTC 偏移）。

### 去重与限速

- **每 IP 冷却**（`dedup_per_ip_seconds`，默认 300 秒）：同一 IP 在冷却期内只报一次；0 = 关闭。
- **断开条跟随连接条**：某次连接的连接通知被冷却压掉时，它的断开通知也不发（`notify_on_connect=false` 时，断开条按同样的每 IP 冷却独立判定）。
- **全局限速**（`max_per_minute`，默认 3）：所有通知共用一个令牌桶——平均每分钟最多 3 条、突发上限 3 条。
- **队满丢新**：通知经有界内存队列（1024 条）发送，队列满时丢弃新条目并记日志——绝不拖慢困住逻辑。
- 发送失败自动重试（最多 3 次：退避 2 秒 / 10 秒 / 30 秒；Telegram 返回 429 时按其 `retry_after` 等待）；最终失败只记日志。
- 通知中的“该 IP 今日第 N 次”按服务器本地日期统计（含被压制的连接）。
- 冷却窗口与“今日第 N 次”计数**只存在内存中**：重启进程后两者清零（同一 IP 会重新上报一次，计数从头开始）。

## 命令行

| 参数 | 行为 |
|---|---|
| `--config <路径>` | 指定配置文件（默认 = 二进制所在目录的 `config.json`） |
| `--version` | 打印 `SSHTarpitWatch <版本>` 后退出 |
| `--help` | 打印用法后退出 |
| `--test-notify` | 加载并校验配置，绕过去重与限速发一条测试通知，然后退出 |

- 退出码：`0` = 成功；`1` = 用法错误或配置错误；`2` = 测试通知发送失败。
- 不带参数 = 启动焦油坑服务。

## 信号

| 信号 | 行为 |
|---|---|
| `SIGTERM` / `SIGINT` | 优雅退出：停止接受新连接 → 关闭现有连接 → 通知队列有限排空（最多 5 秒）→ 打印 TOTALS → 退出 0 |
| `SIGHUP` | 热重载配置；新配置非法或文件缺失时保留旧配置继续运行（不退出） |
| `SIGUSR1` | 打印 TOTALS 统计 |

> systemd 下触发重载：`sudo systemctl kill -s HUP sshtarpitwatch`。

## systemd 服务（可选）

想让 SSHTarpitWatch 开机自启、崩溃后自动重启，可以交给 systemd 托管。服务文件里的路径和运行身份取决于你的环境，所以下面给的是一份**模板**而不是成品；其中两处需要按实际情况填写：`ExecStart`（二进制绝对路径）与 `User`/`Group`（运行身份）。

```ini
[Unit]
Description=SSHTarpitWatch — SSH tarpit with connection notifications
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
# 二进制绝对路径（config.json 与它同目录，无需设置 WorkingDirectory）
ExecStart=/opt/sshtarpitwatch/sshtarpitwatch
Restart=on-failure
RestartSec=5

# 运行身份（自己的用户名即可，无需 root）
User=youruser
Group=youruser

NoNewPrivileges=true
ProtectSystem=strict
PrivateTmp=true
# 若二进制位于 /home 或 /root 下，请去掉下一行——ProtectHome 会把家目录对服务隐藏
#ProtectHome=true
# 仅当监听端口 < 1024 时需要下一行（默认 2222 无需）
#AmbientCapabilities=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
```

```bash
# 1. 写入服务文件（内容为上面的模板，按你的环境填好两处）
sudo $EDITOR /etc/systemd/system/sshtarpitwatch.service

# 2. 启用并启动
sudo systemctl daemon-reload
sudo systemctl enable --now sshtarpitwatch

# 3. 看日志（Ctrl+C 退出）
journalctl -u sshtarpitwatch -f
```

- **配置文件权限**：建议 `chmod 600 config.json`，只有服务运行者可读。
- **改配置后**：`sudo systemctl kill -s HUP sshtarpitwatch` 热重载（新配置非法时保留旧配置继续运行）。
- **端口 < 1024**（如 22；默认 2222 无需）：取消模板里 `AmbientCapabilities=CAP_NET_BIND_SERVICE` 一行的注释，或改用 ≥1024 的端口。
- **卸载**：`sudo systemctl disable --now sshtarpitwatch`，删掉 `/etc/systemd/system/sshtarpitwatch.service`，再删掉解压出来的目录（含 `config.json`）。程序不会在系统其他位置留下文件。

## 运行要求与部署注意

- **发布形式**：linux-x64 原生 AOT 单文件（约 6.5 MB），无需 .NET 运行时、无需 ICU。
- **系统要求**：glibc ≥ 2.34（Debian 12 / Ubuntu 22.04 及以上）；需要系统 libssl（Debian 默认自带）。
- **端口**：默认 2222；端口 < 1024 需要额外权限（见上一节）。

## 客户端会自报哪些信息？

SSH 协议要求客户端在连接建立后立刻发送自己的身份串（如 `SSH-2.0-OpenSSH_9.6p1`）。本工具会把它一并放进通知——这样你一眼就能看出踩坑的是正经 SSH 用户、扫描器、还是僵尸网络。

## Credits

- 机制对标 **[skeeto/endlessh](https://github.com/skeeto/endlessh)**（SSH 焦油坑的经典实现，公有领域）——本项目为**独立实现**（C#），不包含其代码。
- 灵感亦来自 **[shizunge/endlessh-go](https://github.com/shizunge/endlessh-go)**（endlessh 的现代 Go 实现）。
