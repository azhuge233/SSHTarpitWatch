# Command line & signals

[← Back to README](README.md) ｜ [中文](../cli.md)

## Command line

| Option | Behavior |
|---|---|
| `--config <path>` | Configuration file (default: the `config.json` next to the binary) |
| `--version` | Print `SSHTarpitWatch <version>` and exit |
| `--help` | Print usage and exit |
| `--test-notify` | Load and validate the config, send one test notification (bypassing dedup and rate limits), then exit |

- Exit codes: `0` = success; `1` = usage or configuration error; `2` = test notification failed.
- No options = start the tarpit server.

## Signals

| Signal | Behavior |
|---|---|
| `SIGTERM` / `SIGINT` | Graceful shutdown: stop accepting connections → close existing connections → drain the notification queue (up to 5 s) → print TOTALS → exit 0 |
| `SIGHUP` | Reload the config; if the new config is invalid or the file is missing, the old config is kept and the process keeps running |
| `SIGUSR1` | Print TOTALS |

> Trigger a reload under systemd with `sudo systemctl kill -s HUP sshtarpitwatch`; under Docker with `docker kill -s HUP sshtarpitwatch`.
