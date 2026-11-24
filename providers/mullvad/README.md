# Mullvad 官方客户端 + Clash（旧脚本方案）

桌面端 0.2.0 已内置主内核和 helper，无需安装或导入 Clash。新版请按[桌面安装与升级指南](../../desktop/README.md)操作；海外继续使用 Mullvad 官方隧道内 SOCKS5。

以下仅供继续使用旧脚本方案的用户参考。先完成[旧脚本共用准备步骤](../../docs/legacy-script-setup.md#共用准备步骤)。本目录包含 `Clash-Mullvad.yaml` 和订阅级 `Profile-DNS.js`。

## Mullvad 设置

1. 在新机器自行登录官方 Mullvad，并连接所需出口。
2. 设置 → Split tunneling / 分流 → 查找其他应用，添加辅助程序的实际路径 `mullvad-direct-helper.exe`。
3. 主 Clash 内核 `verge-mihomo.exe` 留在 VPN 内。使用 Clash 服务模式，避免 GUI 排除项对子进程的继承影响内核。
4. 允许本地网络访问，供局域网规则使用。已有其他应用排除项按使用者需要保留。

辅助程序名称保留历史命名；它是独立的 Mihomo 内核，不读取 Mullvad 账号或 WireGuard 私钥。

## 导入配置

1. 在 Clash 订阅页导入本目录 `Clash-Mullvad.yaml`，先保持未选中。
2. 打开本目录 `Profile-DNS.js`，复制全部内容。
3. 右键新订阅 → 扩展脚本，粘贴并保存。脚本只绑定到本订阅。
4. 保持规则模式、服务模式，TUN 和 IPv6 关闭。
5. 官方 Mullvad 连接成功后，手动选中新订阅，`Mullvad` 组选择 `Official`，再开启系统代理。

需要个人规则时，把 YAML 复制到根目录 `local/`，使用 `*.local.yaml` 文件名，再导入私人副本。

## 路由和 DNS

| 流量 | 路径 |
|---|---|
| 海外 TCP | `Mullvad → Official → 10.64.0.1:1080 → 官方 VPN` |
| 国内 TCP | `ChinaDirect → 127.0.0.1:18997 → 被排除的辅助程序 → 本地网络` |
| 国内 DoH | `https://223.5.5.5/dns-query#ChinaDirect` |
| 其他 DoH | `https://1.1.1.1/dns-query#Mullvad` |

`Official` 绑定 `Mullvad` 接口。隧道内 SOCKS5 只有在官方客户端连接时可用。无需复制官方客户端某次连接的公网入口、混淆方式或随机端口。

普通 `DIRECT` 跟随系统路由，国内公网规则因此使用辅助程序；局域网规则继续使用 `DIRECT`。订阅脚本整体替换 DNS，覆盖可能冲突的全局 DNS 扩展。

## 实际验证

将 `7897` 替换为 Clash 设置页的实际混合端口：

```powershell
curl.exe --proxy http://127.0.0.1:7897 https://am.i.mullvad.net/json
curl.exe --proxy http://127.0.0.1:7897 -I https://www.baidu.com/
```

- Mullvad 检测应报告 Mullvad 出口，国家与官方客户端一致。
- 国内请求在 Clash 中显示 `ChinaDirect`，海外显示 `Mullvad[Official]`。
- DNS 连接分别使用国内辅助路径和官方 SOCKS5 路径。
- 辅助程序单独访问出口检测服务时，应显示本地宽带出口。

不遵循系统代理的软件需要另行通过官方应用分流处理。真实 VPN 断线、所有软件协议和 UDP 游戏未纳入本方案验收。

## 停止或切回其他线路

由使用者先关闭 Clash 系统代理，再按需要断开 Mullvad、选择其他订阅。双击根目录 `Stop-Helper.cmd` 可停止对应目录的辅助程序。需要恢复 TUN 或 IPv6 时按原配置手动调整。

## 官方资料

- [Mullvad SOCKS5](https://mullvad.net/en/help/socks5-proxy)
- [Mullvad 应用分流](https://mullvad.net/en/help/split-tunneling-with-the-mullvad-app)
- [Windows 分流驱动](https://github.com/mullvad/win-split-tunnel)
- [Clash Verge 扩展顺序与设置优先级](https://www.clashverge.dev/guide/extend.html)
