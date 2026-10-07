# Docker 部署

[← 返回 README](../README.md) ｜ [English](en/docker.md)

镜像与 Release 出自同一个二进制（CI 发版时构建并推送），拉取体积约 8.6 MB——基础镜像是 .NET 官方的 chiseled 变体：没有 shell、没有包管理器、默认非 root。

## 方式一：docker compose（推荐）

仓库根目录带一份示例 [`docker-compose.yml`](../docker-compose.yml)：加固参数、日志上限、时区挂载都已经写在里面，整份文件里需要按你的环境改的只有 **两处**——`user`（改成 `id -u`:`id -g` 的输出）与 `ports`（默认 2222，要让焦油坑顶在宿主 22 端口就写 `"22:2222"`）。

```bash
# 准备配置（compose 文件旁边建 config/ 目录，把配置样例复制进去）
mkdir -p config && cp config.example.json config/config.json
$EDITOR config/config.json
chmod 600 config/config.json

# 一键启动 / 看日志 / 停止
docker compose up -d
docker compose logs -f
docker compose down
```

容器内读的是 `/data/config.json`，而 compose 把 `./config` 挂到了 `/data`——所以配置就放在 compose 文件旁边的 `config/` 目录里（与仓库里 `config.example.json` 的位置一致）。改完配置执行 `docker compose kill -s HUP sshtarpitwatch` 热重载。

## 方式二：docker run

不用 compose 时，等价的一条命令：

```bash
# 1. 拉取镜像
docker pull azhuge233/sshtarpitwatch:<版本>        # 例如 0.2.0；:latest 跟随最新版本

# 2. 准备配置目录（权限照旧建议 600）
mkdir -p ~/sshtarpitwatch
cp config.example.json ~/sshtarpitwatch/config.json
$EDITOR ~/sshtarpitwatch/config.json
chmod 600 ~/sshtarpitwatch/config.json

# 3. 运行（以你自己的 UID 运行，600 的配置才读得到；参数已按最小权限配好）
docker run -d --name sshtarpitwatch \
  --restart unless-stopped \
  --read-only --tmpfs /tmp \
  --cap-drop=ALL --security-opt no-new-privileges:true \
  --pids-limit 256 --memory 128m \
  --user "$(id -u):$(id -g)" \
  -p 2222:2222 \
  -v ~/sshtarpitwatch:/data:ro \
  -v /etc/localtime:/etc/localtime:ro \
  azhuge233/sshtarpitwatch:<版本>

# 4. 看日志（Ctrl+C 退出）
docker logs -f sshtarpitwatch
```

## 两种方式共同的说明

- **镜像标签**：每个版本一个（如 `:0.2.0`），另推 `:latest` 跟随最新版本。
- **端口**：默认 `-p 2222:2222`；想让焦油坑顶在宿主的 22 端口（真实 SSH 另挪他处）写 `-p 22:2222` 即可——容器里始终监听 2222，不需要任何额外权限。
- **配置**：容器读 `/data/config.json`，挂载的是**目录**而不是单个文件——编辑器保存会替换文件，单文件挂载下热重载会读到旧内容。
- **权限**：以你自己的身份运行（compose 里的 `user:` 或 `docker run` 的 `--user`），`chmod 600` 的配置依然可读；容器也不会改动宿主上的任何文件。
- **时区**：镜像不含时区数据（体积小的一部分原因），挂上 `/etc/localtime` 通知时间就与宿主机一致；不挂则显示 UTC。
- **日志**：全部走 stdout（`docker logs` / `docker compose logs`）；compose 示例里已限制日志文件大小为 10m×3。
- **卸载**：`docker compose down`（或 `docker rm -f sshtarpitwatch`），再删掉配置目录即可。
- **防火墙**：Docker 发布端口是直接写 iptables 的，会绕过 ufw 一类的规则——暴露面完全由 `ports` / `-p` 参数决定。
