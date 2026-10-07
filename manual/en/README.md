# SSHTarpitWatch

[中文](../../README.md) ｜ **English**

An SSH tarpit with connection notifications: keep scanners and brute-forcers stuck, and get notified the moment someone steps in.

> **About this repository**: the code and documentation are **entirely AI-generated** (a human sets the direction and signs off).

## What it does

It runs a fake SSH server (port configurable, default 2222):

- Anyone who tries to SSH into it is slowly fed random data — like [skeeto/endlessh](https://github.com/skeeto/endlessh), the handshake never completes and the connection hangs for hours or days, wasting the attacker's connections and patience. Your real SSH service stays on another port, unaffected.
- On every connection (and disconnection) it sends you a Telegram notification with:
  - source IP and port, and the probed local port
  - the client's **identification string** (e.g. `SSH-2.0-libssh2_1.11.0`) — you instantly see what tool it is
  - time, and the per-IP daily connection count
  - on disconnect: how long it was trapped and how many bytes were sent
- Configurable instance **nickname**, so you can tell machines apart when they share one notification channel.
- Built-in **deduplication and rate limiting** to avoid notification floods.

Reading the client identification string and sending notifications never affect the tarpit: notification failures (timeouts, rate limits, network errors) are only logged, and the tarpit keeps running.

## Quick start

The program is a single linux-x64 binary (about 6.5 MB, no .NET runtime required). The archive contains 2 files: `sshtarpitwatch` (the binary) and `config.example.json` (example config). The directory you unpack into becomes its run directory, with `config.json` next to the binary (see [Configuration](configuration.md)). Example (Debian 13):

```bash
# 1. Unpack (file name looks like sshtarpitwatch-<version>-linux-x64.tar.gz)
tar -xzf sshtarpitwatch-<version>-linux-x64.tar.gz
cd sshtarpitwatch-<version>-linux-x64

# 2. Copy the example config and fill in your Telegram bot token and chat ID
cp config.example.json config.json
$EDITOR config.json

# 3. Start it (listens on port 2222 by default; Ctrl+C to stop)
./sshtarpitwatch

# 4. Send a test notification to check your credentials and network
./sshtarpitwatch --test-notify

# 5. Start at boot / restart on failure: see systemd.md or docker.md
```

> `--test-notify` exit codes: `0` = sent successfully; `1` = configuration error (including a disabled channel or missing credentials); `2` = sending failed.

## Documentation

| Document | Content |
|---|---|
| [Configuration](configuration.md) | Config file location, every key with defaults, a minimal example |
| [Notifications](notifications.md) | What the messages look like, deduplication and rate limiting |
| [Command line & signals](cli.md) | Options, exit codes, SIGTERM/SIGHUP/SIGUSR1 |
| [systemd deployment](systemd.md) | Service file template and notes |
| [Docker deployment](docker.md) | One-command compose start, image usage, hardening flags, ports and permissions |
| [中文文档](../../README.md) | The Chinese originals |

## Requirements and deployment notes

- **Release form**: single linux-x64 native AOT binary (about 6.5 MB) — no .NET runtime required, no ICU needed.
- **System requirements**: glibc ≥ 2.34 (Debian 12 / Ubuntu 22.04 or newer); system libssl required (shipped with Debian by default).
- **Port**: default 2222; ports below 1024 need extra privilege — both deployment methods have their own answer (see [systemd](systemd.md) / [Docker](docker.md)).

## What client information is captured?

The SSH protocol requires clients to send their identification string (e.g. `SSH-2.0-OpenSSH_9.6p1`) immediately after connecting. This tool forwards it in the notification, so you can tell real SSH clients, scanners, and botnets apart at a glance.

## Credits

- Mechanism modeled after **[skeeto/endlessh](https://github.com/skeeto/endlessh)** (public domain). This project is an **independent implementation** in C# and contains none of its code.
- Inspired by **[shizunge/endlessh-go](https://github.com/shizunge/endlessh-go)**, a modern Go implementation of endlessh.
