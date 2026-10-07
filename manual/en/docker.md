# Docker deployment

[← Back to README](README.md) ｜ [中文](../docker.md)

The image is built from the same binary as the release (CI builds and pushes it on every release). Pull size is about 8.6 MB — the base image is the official .NET "chiseled" variant: no shell, no package manager, non-root by default.

## Option 1: docker compose (recommended)

The repository root ships an example [`docker-compose.yml`](../../docker-compose.yml): the hardening flags, the log limits and the timezone mount are already in it. The only **two** spots to change for your setup are `user` (set it to your `id -u`:`id -g`) and `ports` (2222 by default; use `"22:2222"` to put the tarpit on the host's port 22). The file's comments are in Chinese — the project's primary documentation language.

```bash
# prepare the config (create a config/ directory next to the compose file)
mkdir -p config && cp config.example.json config/config.json
$EDITOR config/config.json
chmod 600 config/config.json

# one-command start / logs / stop
docker compose up -d
docker compose logs -f
docker compose down
```

The container reads `/data/config.json`, and compose mounts `./config` at `/data` — so the config lives in the `config/` directory next to the compose file (the same place as the repository's `config.example.json`). After editing it, `docker compose kill -s HUP sshtarpitwatch` reloads it.

## Option 2: docker run

The equivalent single command when you are not using compose:

```bash
# 1. Pull the image
docker pull azhuge233/sshtarpitwatch:<version>     # e.g. 0.2.0; :latest follows the newest release

# 2. Prepare a config directory (600 is still recommended)
mkdir -p ~/sshtarpitwatch
cp config.example.json ~/sshtarpitwatch/config.json
$EDITOR ~/sshtarpitwatch/config.json
chmod 600 ~/sshtarpitwatch/config.json

# 3. Run (as your own UID so the 600 config stays readable; the flags are already minimal-privilege)
docker run -d --name sshtarpitwatch \
  --restart unless-stopped \
  --read-only --tmpfs /tmp \
  --cap-drop=ALL --security-opt no-new-privileges:true \
  --pids-limit 256 --memory 128m \
  --user "$(id -u):$(id -g)" \
  -p 2222:2222 \
  -v ~/sshtarpitwatch:/data:ro \
  -v /etc/localtime:/etc/localtime:ro \
  azhuge233/sshtarpitwatch:<version>

# 4. Follow the logs (Ctrl+C to exit)
docker logs -f sshtarpitwatch
```

## Notes for both options

- **Image tags**: one per version (e.g. `:0.2.0`), plus `:latest` following the newest release.
- **Port**: `-p 2222:2222` by default; to put the tarpit on the host's port 22 (moving your real SSH elsewhere) use `-p 22:2222` — the container always listens on 2222 and needs no extra privileges.
- **Config**: the container reads `/data/config.json`, and the mount is a **directory**, not a single file — editors replace files when saving, and with a single-file mount a reload would read stale content.
- **Permissions**: run as yourself (the compose `user:` or `docker run --user`), so a `chmod 600` config stays readable and the container never modifies host files.
- **Time zone**: the image ships no timezone data (part of why it is so small); mount `/etc/localtime` to match the host, otherwise times show UTC.
- **Logs**: everything goes to stdout (`docker logs` / `docker compose logs`); the compose example already caps log files at 10m×3.
- **Uninstall**: `docker compose down` (or `docker rm -f sshtarpitwatch`), then delete the config directory.
- **Firewall**: Docker publishes ports by writing iptables rules directly, bypassing ufw-style rules — your exposure is exactly what `ports` / `-p` says.
