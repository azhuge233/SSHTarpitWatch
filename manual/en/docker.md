# Docker deployment

[← Back to README](README.md) ｜ [中文](../docker.md)

The image is built from the same binary as the release (CI builds and pushes it on every release). Pull size is about 8.6 MB — the base image is the official .NET "chiseled" variant: no shell, no package manager.

## Option 1: docker compose (recommended)

The repository root ships an example [`docker-compose.yml`](../../docker-compose.yml): the hardening flags, the log limits and the timezone mount are already in it. What usually needs adjusting for your setup is just `server.port` in the config. The file's comments are in Chinese — the project's primary documentation language.

**The example uses the host network** (`network_mode: host`): the container shares the host's network stack and therefore sees the **real client address**. That matters a lot for the notification content — under the bridge network Docker's port forwarding rewrites the source to the bridge gateway (e.g. `172.20.0.1`), which falsifies the reported source, the geolocation (GeoIP), the ISP tags, the per-IP deduplication and the "today's Nth" counter; IPv6 clients suffer most: the connection first reaches the host's IPv6 port and is then re-originated as an IPv4 connection into the container, losing the real address entirely. With the host network there is no such forwarding — both v4 and v6 arrive as they are.

```bash
# prepare the config (create a config/ directory next to the compose file)
mkdir -p config && cp config.example.json config/config.json
$EDITOR config/config.json     # set the port here: server.port (2222 by default; use 22 to sit on the host's port 22)
chmod 600 config/config.json

# one-command start / logs / stop
docker compose up -d
docker compose logs -f
docker compose down
```

The container reads `/data/config.json`, and compose mounts `./config` at `/data` — so the config lives in the `config/` directory next to the compose file (the same place as the repository's `config.example.json`). After editing it, `docker compose kill -s HUP sshtarpitwatch` reloads it.

> **Why root here**: binding a low port such as 22 is a kernel requirement — a non-root process cannot do it even when granted the `NET_BIND_SERVICE` capability (measured: `Permission denied`). The example therefore runs the container process as `user: "0:0"` with **only that one capability added back**; the read-only root filesystem, `cap_drop: ALL`, `no-new-privileges`, and the memory/process limits all stay in place, and the image contains no shell — so the practically reachable attack surface stays small.

## Option 2: docker run

The equivalent single command when you are not using compose:

```bash
# 1. Pull the image
docker pull azhuge233/sshtarpitwatch:<version>     # e.g. 0.3.1; :latest follows the newest release

# 2. Prepare a config directory (600 is still recommended)
mkdir -p ~/sshtarpitwatch
cp config.example.json ~/sshtarpitwatch/config.json
$EDITOR ~/sshtarpitwatch/config.json    # set server.port to the port to listen on
chmod 600 ~/sshtarpitwatch/config.json

# 3. Run (host network + root with only NET_BIND_SERVICE; the remaining flags are already minimal-privilege)
docker run -d --name sshtarpitwatch \
  --restart unless-stopped \
  --network host \
  --user 0:0 --cap-drop=ALL --cap-add=NET_BIND_SERVICE \
  --read-only --tmpfs /tmp \
  --security-opt no-new-privileges:true \
  --pids-limit 256 --memory 128m \
  -v ~/sshtarpitwatch:/data:ro \
  -v /etc/localtime:/etc/localtime:ro \
  azhuge233/sshtarpitwatch:<version>

# 4. Follow the logs (Ctrl+C to exit)
docker logs -f sshtarpitwatch
```

## Notes for both options

- **Image tags**: one per version (e.g. `:0.3.1`), plus `:latest` following the newest release.
- **Port**: set by `server.port` in the config (2222 in the example; use 22 to take the host's port 22). With the host network the port is exclusive — nothing else on the host may listen on it.
- **Source address**: with the host network the container sees the real client address (v4 and v6 alike); that is the precondition for accurate geolocation, ISP tags and per-IP deduplication.
- **Config**: the container reads `/data/config.json`, and the mount is a **directory**, not a single file — editors replace files when saving, and with a single-file mount a reload would read stale content.
- **Permissions**: the container process runs as root holding only `NET_BIND_SERVICE` (needed for the low port); all other minimal-privilege settings are unchanged. The process writes no files and the container never modifies host files; a `chmod 600` config stays readable.
- **Firewall**: with the host network Docker no longer writes iptables rules on your behalf — your exposure is exactly the one port in the config, and the host's own firewall rules apply to it normally.
- **Time zone**: the image ships no timezone data (part of why it is so small); mount `/etc/localtime` to match the host, otherwise times show UTC.
- **Logs**: everything goes to stdout (`docker logs` / `docker compose logs`); the compose example already caps log files at 10m×3.
- **IP geolocation databases (optional)**: put the database files (`.mmdb` / `.xdb`) in the **same directory as `config.json`** — compose mounts `./config` at `/data` and relative paths resolve against the config file's directory, so dropping them into `config/` is enough.
- **Uninstall**: `docker compose down` (or `docker rm -f sshtarpitwatch`), then delete the config directory.
