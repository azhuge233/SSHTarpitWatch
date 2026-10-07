# 命令行与信号

[← 返回 README](../README.md) ｜ [English](en/cli.md)

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

> systemd 下触发重载：`sudo systemctl kill -s HUP sshtarpitwatch`；Docker 下：`docker kill -s HUP sshtarpitwatch`。
