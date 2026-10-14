# Docker 部署

[← 返回 README](../README.md) ｜ [English](en/docker.md)

镜像与 Release 出自同一个二进制（CI 发版时构建并推送），拉取体积约 8.6 MB——基础镜像是 .NET 官方的 chiseled 变体：没有 shell、没有包管理器。

## 方式一：docker compose（推荐）

仓库根目录带一份示例 [`docker-compose.yml`](../docker-compose.yml)：加固参数、日志上限、时区挂载都已经写在里面，需要按环境调整的通常只有配置里的 `server.port`。

**示例使用 host 网络**（`network_mode: host`）：容器共享宿主网络栈，看到的是真实客户端地址。这对通知内容很关键——bridge 网络下 Docker 的端口转发会把来源改写成网桥网关（例如 `172.20.0.1`），于是通知里的来源、地区解析（GeoIP）、ISP 标签、按 IP 去重与「今日第 N 次」全部失真；外部 IPv6 客户端尤其明显：连接先到宿主的 IPv6 端口，再被转生成一条 IPv4 连接进容器，真实地址就此丢失。host 网络没有这层转发，v4 与 v6 都是真实地址。

```bash
# 准备配置（compose 文件旁边建 config/ 目录，把配置样例复制进去）
mkdir -p config && cp config.example.json config/config.json
$EDITOR config/config.json     # 端口就在这里改：server.port（示例默认 2222；想顶在宿主 22 端口就写 22）
chmod 600 config/config.json

# 一键启动 / 看日志 / 停止
docker compose up -d
docker compose logs -f
docker compose down
```

容器内读的是 `/data/config.json`，而 compose 把 `./config` 挂到了 `/data`——所以配置就放在 compose 文件旁边的 `config/` 目录里（与仓库里 `config.example.json` 的位置一致）。改完配置执行 `docker compose kill -s HUP sshtarpitwatch` 热重载。

> **为什么这里是 root**：监听 22 这类 <1024 端口是内核硬要求——非 root 进程即使被授予 `NET_BIND_SERVICE` 能力也不生效（实测 `Permission denied`）。示例因此把容器内进程配成 `user: "0:0"` + **只放回这一项能力**；只读根文件系统、`cap_drop: ALL`、`no-new-privileges`、内存与进程上限全部照旧，镜像内也没有 shell，实际可用的攻击面很小。

## 方式二：docker run

不用 compose 时，等价的一条命令：

```bash
# 1. 拉取镜像
docker pull azhuge233/sshtarpitwatch:<版本>        # 例如 0.3.1；:latest 跟随最新版本

# 2. 准备配置目录（权限照旧建议 600）
mkdir -p ~/sshtarpitwatch
cp config.example.json ~/sshtarpitwatch/config.json
$EDITOR ~/sshtarpitwatch/config.json    # server.port 填要监听的端口
chmod 600 ~/sshtarpitwatch/config.json

# 3. 运行（host 网络 + root 只带 NET_BIND_SERVICE；其余参数已按最小权限配好）
docker run -d --name sshtarpitwatch \
  --restart unless-stopped \
  --network host \
  --user 0:0 --cap-drop=ALL --cap-add=NET_BIND_SERVICE \
  --read-only --tmpfs /tmp \
  --security-opt no-new-privileges:true \
  --pids-limit 256 --memory 128m \
  -v ~/sshtarpitwatch:/data:ro \
  -v /etc/localtime:/etc/localtime:ro \
  azhuge233/sshtarpitwatch:<版本>

# 4. 看日志（Ctrl+C 退出）
docker logs -f sshtarpitwatch
```

## 两种方式共同的说明

- **镜像标签**：每个版本一个（如 `:0.3.1`），另推 `:latest` 跟随最新版本。
- **端口**：由配置文件的 `server.port` 决定（示例默认 2222；要 22 就写 22）。host 模式下端口是宿主独占的——宿主上不能有别的程序监听同一端口。
- **来源地址**：host 网络下容器看到的就是真实客户端地址（v4 与 v6 均如此）；这是地区解析、ISP 标签与按 IP 去重准确的前提。
- **配置**：容器读 `/data/config.json`，挂载的是**目录**而不是单个文件——编辑器保存会替换文件，单文件挂载下热重载会读到旧内容。
- **权限**：容器内进程为 root，但只持有 `NET_BIND_SERVICE` 一项能力（低端口所需），其余最小权限设置照旧；进程不写任何文件，容器也不改动宿主上的任何文件，`chmod 600` 的配置照常可读。
- **防火墙**：host 网络下 Docker 不再代写 iptables 规则，对外暴露面就是配置里那一个端口，宿主自身的防火墙规则对它正常生效。
- **时区**：镜像不含时区数据（体积小的一部分原因），挂上 `/etc/localtime` 通知时间就与宿主机一致；不挂则显示 UTC。
- **日志**：全部走 stdout（`docker logs` / `docker compose logs`）；compose 示例里已限制日志文件大小为 10m×3。
- **IP 地区库（可选）**：把库文件（`.mmdb` / `.xdb`）与 `config.json` 放在**同一个目录**——compose 已把 `./config` 挂到 `/data`，相对路径以配置文件目录为基准，所以放进 `config/` 就好。
- **卸载**：`docker compose down`（或 `docker rm -f sshtarpitwatch`），再删掉配置目录即可。
