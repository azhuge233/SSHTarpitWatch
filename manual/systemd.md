# systemd 部署

[← 返回 README](../README.md) ｜ [English](en/systemd.md)

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
