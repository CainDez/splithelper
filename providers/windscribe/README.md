# Windscribe 官方网关 + Clash（旧脚本方案）

**桌面端 0.2.0 不需要本页的代理网关、认证和 Clash 导入步骤。** 新版直接绑定 Windscribe IKEv2 / WireGuard 接口，请按[桌面安装与升级指南](../../desktop/README.md)操作。没有其他设备使用时，旧 Proxy Gateway 可以关闭；VPN 连接和 helper 排除保留。

以下仅供继续使用旧脚本方案的用户参考。先完成[旧脚本共用准备步骤](../../docs/legacy-script-setup.md#共用准备步骤)。本目录的 `Clash-Windscribe.example.yaml` 是待填写的示例，`192.0.2.1` 为文档专用地址，用户名和密码均为占位符。

## 1. 在 Windscribe 排除辅助程序

1. 自行登录官方 Windscribe，并连接所需出口。
2. Preferences → Connection → Split Tunneling，启用 **Exclusive / 排除模式**。
3. 添加实际运行目录中的 `mullvad-direct-helper.exe`。
4. 主 `verge-mihomo.exe` 和 `Windscribe.exe` 保持在 VPN 内。

辅助程序无需因 VPN 更换而重命名。若已有一个运行正常的辅助程序，继续使用其实际路径和启动项。

## 2. 开启官方 SOCKS 网关

Preferences → Connection → Proxy Gateway：

- 开启网关，代理类型选择 **SOCKS**。
- 开启 **Require Authentication / 要求认证**。
- 开启 **Only while connected / 仅当已连接 VPN 时**。
- 将网关端口设为一个固定可用值，例如 `61053`。
- 按客户端要求允许网关所需的 LAN 流量；系统权限提示由使用者处理。

记录界面显示的 **IP、端口、网关用户名和网关密码**。这是本地网关的凭据，不是 Windscribe 账号密码。网关绑定本机局域网地址，不能假定是 `127.0.0.1`；更换网络导致本机地址变化时需同步更新配置。

## 3. 创建私人配置副本

在仓库根目录执行：

```powershell
New-Item -ItemType Directory -Path .\local -Force | Out-Null
Copy-Item .\providers\windscribe\Clash-Windscribe.example.yaml .\local\Clash-Windscribe.local.yaml
```

用文本编辑器打开 `local/Clash-Windscribe.local.yaml`，只替换 `Official` 节点的字段：

| 字段 | 填写内容 |
|---|---|
| `server` | 网关界面显示的本机 IP，替换文档地址 `192.0.2.1` |
| `port` | 网关固定端口，默认示例为 `61053` |
| `username` | 替换 `CHANGE_ME_GATEWAY_USERNAME` |
| `password` | 替换 `CHANGE_ME_GATEWAY_PASSWORD` |

保持字符串的 YAML 引号；含特殊字符时按 YAML 字符串语法转义。`Official` 节点不绑定旧的 Mullvad 接口。`ChinaDirect` 仍指向 `127.0.0.1:18997`。

`local/` 和 `*.local.*` 已被 Git 忽略。在本地副本填写信息，仓库示例保留占位符。

## 4. 导入 Clash

1. 导入 `local/Clash-Windscribe.local.yaml`，先保持未选中。
2. 打开本目录 `Profile-DNS.js`，复制全部内容。
3. 右键新订阅 → 扩展脚本，粘贴保存，只绑定到此订阅。
4. 核对运行配置：规则模式、服务模式、TUN 关闭、IPv6 关闭，`find-process-mode: strict`。
5. 保持 Windscribe 已连接，手动选中新订阅，`Windscribe` 组选择 `Official`。
6. 手动开启 Clash 系统代理，随后按下方方法验证。

订阅脚本整体替换 DNS，移除可能遗留的根级接口绑定。Clash 应用自身接管的 TUN、IPv6 和端口仍应在设置页核对。

## 系统代理回环与修复

在已验证的 Windows / Windscribe 2.24.13 环境中，关闭系统代理时网关请求正常；开启后，Windscribe 网关把外连请求再次交给 Clash，Clash 又选择相同网关，导致连接递归增长和检测超时。仅把 Windscribe 上游代理设为“无”没有解决该回环。

模板在原有通用规则前增加：

```yaml
rules:
- PROCESS-NAME,Windscribe.exe,DIRECT
# 后面仍为原有的国内、海外、局域网和拦截规则。
```

主 Clash 内核仍处于 VPN 内，因此这里的 `DIRECT` 按系统 Windscribe VPN 路由发出请求，避免再进入 `Official` 网关。它改变 Windscribe 进程的匹配优先级；普通应用继续使用后面的分流规则，国内请求继续经 `ChinaDirect` 走本地宽带。

模板显式使用 `find-process-mode: strict`。验收时需要看到 Windscribe 回送连接被识别为 `Windscribe.exe`，命中 `ProcessName` 规则并使用 `DIRECT`。Windscribe 到 Clash 的连接仍可存在，重点是它们成功出站，数量不再递归增长。

## 路由和 DNS

| 流量 | 路径 |
|---|---|
| 海外 TCP | `Windscribe → Official → 认证 SOCKS 网关 → VPN` |
| 国内 TCP | `ChinaDirect → 本机辅助程序 → 本地宽带` |
| 网关回送的 Windscribe 请求 | `ProcessName → DIRECT → 系统 VPN 路由` |
| 国内 DoH | `https://223.5.5.5/dns-query#ChinaDirect` |
| 其他 DoH | `https://1.1.1.1/dns-query#Windscribe` |

## 在系统代理开启时验收

`7897` 应替换为 Clash 设置页实际显示的混合端口：

```powershell
curl.exe --proxy http://127.0.0.1:7897 https://www.cloudflare.com/cdn-cgi/trace
curl.exe --proxy http://127.0.0.1:7897 https://www.google.com/generate_204 -I
curl.exe --proxy http://127.0.0.1:7897 https://www.baidu.com/ -I
curl.exe --proxy socks5h://127.0.0.1:18997 https://www.cloudflare.com/cdn-cgi/trace
```

- 海外出口国家应与 Windscribe 当前出口一致；辅助程序出口应与本地宽带一致。
- 国内站点在 Clash 中使用 `ChinaDirect`，海外使用 `Official / Windscribe`。
- 国内、海外 DoH 分别经过辅助路径和网关路径。
- Windscribe 回送请求命中首条进程规则，重复 Official 延迟探测和网站访问均成功。
- 若界面仍显示先前的 Timeout，可重新检测，并结合实际请求和连接路由判断。

示例仅覆盖 TCP 系统代理。真实 VPN 断线验收、UDP 游戏、全系统 TUN、长期自动重连和全部客户端功能未纳入已验证范围。“仅连接时共享”不应被当成整套组合已经通过断线防泄漏验收。

## 常见问题

- **关闭系统代理正常，开启就超时**：核对首条进程规则、实际进程名称、`find-process-mode` 以及主 Clash 仍在 VPN 内。编辑订阅后需要重新激活，再查看实际运行配置。
- **网关连接失败**：确认官方 VPN 已连接、类型为 SOCKS、IP 和固定端口仍与客户端一致，填写的是网关认证信息。
- **国内请求仍走 VPN**：核对当前运行辅助程序的实际路径是否位于 Windscribe 排除列表，国内规则是否选择 `ChinaDirect`。
- **DNS 被旧配置覆盖**：将本目录脚本绑定到 Windscribe 订阅，并查看运行配置中的国内外 DoH 引用。
- **软件没有经过 Clash**：只有遵循系统代理的流量由这些规则接管；其他软件按官方 VPN 的应用分流设置处理。Windscribe 也可能对系统 DNS 应用自身策略。

## 官方资料

- [Windscribe Proxy Gateway](https://windscribe.com/knowledge-base/articles/what-is-the-proxy-gateway-feature-and-how-to-use-it)
- [Windscribe Split Tunneling](https://windscribe.com/features/split-tunneling)
- [Mihomo SOCKS5 配置](https://wiki.metacubex.one/config/proxies/socks/)
- [Mihomo 进程规则](https://wiki.metacubex.one/config/rules/#process-name)
- [Clash Verge 扩展顺序与设置优先级](https://www.clashverge.dev/guide/extend.html)
