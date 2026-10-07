# systemd deployment

[← Back to README](README.md) ｜ [中文](../systemd.md)

To have systemd manage SSHTarpitWatch (start at boot, restart on failure), you write the service file yourself. Its paths and run identity depend on your setup, so what follows is a **template** rather than a ready-made file; two spots need your values: `ExecStart` (absolute path to the binary) and `User`/`Group` (the account it runs as).

```ini
[Unit]
Description=SSHTarpitWatch — SSH tarpit with connection notifications
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
# absolute path to the binary (config.json sits next to it, so WorkingDirectory is not needed)
ExecStart=/opt/sshtarpitwatch/sshtarpitwatch
Restart=on-failure
RestartSec=5

# the account it runs as (your own username is fine; root is not needed)
User=youruser
Group=youruser

NoNewPrivileges=true
ProtectSystem=strict
PrivateTmp=true
# if the binary lives under /home or /root, remove the next line — ProtectHome hides home directories from the service
#ProtectHome=true
# enable the next line only for ports below 1024 (not needed for the default 2222)
#AmbientCapabilities=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
```

```bash
# 1. Write the service file (the template above, with the two spots filled in for your setup)
sudo $EDITOR /etc/systemd/system/sshtarpitwatch.service

# 2. Enable and start it
sudo systemctl daemon-reload
sudo systemctl enable --now sshtarpitwatch

# 3. Follow the logs (Ctrl+C to exit)
journalctl -u sshtarpitwatch -f
```

- **Config file permissions**: `chmod 600 config.json` so that only the user running the service can read it.
- **After editing the config**: hot-reload with `sudo systemctl kill -s HUP sshtarpitwatch` (an invalid config is rejected and the old one keeps running).
- **Ports below 1024** (e.g. 22; not needed for the default 2222): uncomment `AmbientCapabilities=CAP_NET_BIND_SERVICE`, or use a port ≥1024.
- **Uninstall**: `sudo systemctl disable --now sshtarpitwatch`, remove `/etc/systemd/system/sshtarpitwatch.service`, then delete the unpacked directory (including `config.json`). The program leaves no files anywhere else on the system.
