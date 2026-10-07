# SSHTarpitWatch

**中文** ｜ [English](manual/en/README.md)

SSH 焦油坑 + 连接通知：把扫描器和暴力破解者吊住，并把踩坑消息第一时间推送到你的手机上。

> **关于本仓库**：代码与文档**全部由 AI 生成**（人工负责方向决策与验收）。

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

SSHTarpitWatch 是 linux-x64 单文件程序（约 6.5 MB，无需 .NET 运行时）；包内含 2 件：`sshtarpitwatch`（二进制）、`config.example.json`（配置样例）。解压出来的目录就是它的运行目录——`config.json` 与二进制放在一起（见[配置](manual/configuration.md)）。以 Debian 13 为例：

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

# 5. 开机自启 / 崩溃重启：见 manual/systemd.md 或 manual/docker.md
```

> `--test-notify` 退出码：`0` = 发送成功；`1` = 配置错误（含渠道未启用、凭据缺失）；`2` = 发送失败。

## 文档

| 文档 | 内容 |
|---|---|
| [配置](manual/configuration.md) | 配置文件位置、全部键与默认值、最小配置示例 |
| [通知](manual/notifications.md) | 通知长什么样、去重与限速规则 |
| [命令行与信号](manual/cli.md) | 参数、退出码、SIGTERM / SIGHUP / SIGUSR1 |
| [systemd 部署](manual/systemd.md) | 服务文件模板与说明 |
| [Docker 部署](manual/docker.md) | compose 一键启动、镜像用法、加固参数、端口与权限 |
| [English manual](manual/en/README.md) | 英文版全部文档 |

## 运行要求与部署注意

- **发布形式**：linux-x64 原生 AOT 单文件（约 6.5 MB），无需 .NET 运行时、无需 ICU。
- **系统要求**：glibc ≥ 2.34（Debian 12 / Ubuntu 22.04 及以上）；需要系统 libssl（Debian 默认自带）。
- **端口**：默认 2222；端口 < 1024 需要额外权限——两种部署方式各有处理（见 [systemd 部署](manual/systemd.md) / [Docker 部署](manual/docker.md)）。

## 客户端会自报哪些信息？

SSH 协议要求客户端在连接建立后立刻发送自己的身份串（如 `SSH-2.0-OpenSSH_9.6p1`）。本工具会把它一并放进通知——这样你一眼就能看出踩坑的是正经 SSH 用户、扫描器、还是僵尸网络。

## Credits

- 机制对标 **[skeeto/endlessh](https://github.com/skeeto/endlessh)**（SSH 焦油坑的经典实现，公有领域）——本项目为**独立实现**（C#），不包含其代码。
- 灵感亦来自 **[shizunge/endlessh-go](https://github.com/shizunge/endlessh-go)**（endlessh 的现代 Go 实现）。
