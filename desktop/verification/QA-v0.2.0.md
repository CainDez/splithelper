# 0.2.0 隔离验证、桌面迁移与安装后验收

日期：2026-11-10～11，Windows 本机，.NET SDK 10.0.403。内核为未修改的 Mihomo 1.19.33；Windscribe 官方客户端维护 VPN。第一阶段使用独立数据目录、命名管道和 19097 端口，复用已安装且已排除的 helper，不替换用户的 0.1.1 安装。第二阶段由用户安装 0.2.0 后，对实际安装版验收；两阶段证据分开记录。

## 第一阶段：隔离验证与迁移

| 检查 | 观察结果 |
|---|---|
| 无网关连接 | IKEv2、WireGuard 均可通过绑定 VPN 接口的 VPNDirect 访问海外，国内使用 ChinaDirect/helper；不需要 Windscribe Proxy Gateway |
| IKEv2 断线 | 新请求、复用连接及外部 DNS 被阻断；国内仍可访问。对照请求可通过物理网络访问同一个测试站点，排除“目标站点本身不可达”的假阳性 |
| 缓存与恢复 | Mihomo 接口缓存会造成约 20 秒恢复延迟；同 PID 重载配置清除缓存后恢复，无需重启内核 |
| WireGuard 断线 | 2026-11-11 实测：默认代理保持指向隔离内核；已有连接和新建海外请求失败；约 28.6 秒后仍阻断，helper 出口仍为 CN |
| WireGuard 自动重连 | 生产 ConnectionService.MaintainAsync 约 12.3 秒恢复，协议保持 WireGuard；实际链路检测通过 |
| 旧协议绑定接续 | 把隔离配置设为旧 IKEv2 绑定，在当前 WireGuard 上调用维护逻辑，自动改绑并恢复，主内核 PID 不变 |
| 主内核恢复 | 停止隔离主内核后，维护逻辑重新启动自有内核并恢复代理访问 |
| 默认和 RAS 代理 | 原始设置不同的默认与 IKEv2 连接分别备份、接管、还原；外部修改可被检测，维护逻辑拒绝覆盖，停止时保留外部值 |
| 旧环境保护 | 测试后默认/RAS 代理还原，安装版 journal 哈希及 helper PID 不变；当前 WireGuard 连接恢复 |
| 自动检查 | Debug 与 Release 均 90/90 项测试通过；覆盖规则参数迁移、仅允许 VPN 接口、组件校验/进程身份、旧偏好与新配置不一致时的恢复、代理记录及冲突检测 |
| 原生 UI | 六个隔离 WPF 场景渲染，检查了概览、VPN 指引和窄窗口启动设置；自包含程序只读模式下实际点击环境→VPN→一键启用→自动启动，随后退出 |
| 本地交付 | 自包含发布、NSIS 安装包及便携 ZIP 构建成功；版本 0.2.0；工作树修改状态写入 build-info.json |
| 中断恢复回归 | 模拟配置已更新、偏好仍保存旧 Provider/端口/接口，再停止内核；维护逻辑按已准备配置恢复，实际访问通过 |
| 取消自动恢复 | 取消已有会话的自动恢复后，代理仍生效，journal 哈希不变，主内核存活 |
| 可选旧配置损坏 | 还原后无本机代理依赖时，无关的无效 Clash YAML 不阻止停止或更新准备 |

独立审查指出的恢复回滚、设置错位、代理检查与写入间的覆盖、普通规则 src 参数、无关旧配置阻止停止等问题已作局部修正。已接管代理只校验，不在维护时写回；新接管项写入前再次核对 Original。WinINet 不提供原子的比较并交换，本次没有声称消除所有外部同时修改窗口。

修复后的独立只读复查确认五项问题已闭环，无需再次修改。最终安装包和便携包均通过 7-Zip 完整性检查、SHA-256 核验；包内主内核、geosite.dat 和 Country.mmdb 与固定清单一致。

真实 Chrome 的 B 站视频连接已交叉核对浏览器 TCP 源端口与内核连接：upos-sz-estgoss.bilivideo.com → ChinaDirect，下载 8,366,913 字节；视频 currentTime 从约 12 秒推进至约 45 秒，readyState=4，缓冲继续增长。海外 example.com → VPNDirect/Windscribe。此项使用 IKEv2 隔离主内核，不把入口页面 HTTP 200 当作视频分流证据。

## 第二阶段：安装后验收

2026-11-11，用户完成 0.1.1 → 0.2.0 安装升级后，在实际安装目录运行的桌面端上验证；Windscribe 使用 WireGuard，测试期间保留用户选择的节点和协议。

| 检查 | 观察结果 |
|---|---|
| 安装内容 | 版本 0.2.0；应用、主内核、helper、geosite.dat 和 Country.mmdb 与已交付本地包 SHA-256 相同 |
| 独立运行 | 主内核 127.0.0.1:17897，helper 127.0.0.1:18997；未运行 Clash 内核；实际配置不依赖 Windscribe 代理网关 |
| 代理与配置 | 当前有效代理属于助手，无 PAC/代理守卫冲突；524 条规则与生成结果一致；海外 DNS 指定 VPNDirect |
| 出口与访问 | helper 出口 CN；海外主内核出口与 VPN 出口一致；百度、B 站、Google、GitHub 在退出重开前后均可访问 |
| 实际视频流 | Chrome TCP 源端口与内核连接关联，两条 bilivideo CDN 连接均为 ChinaDirect，累计接收约 25.0 MB；1080p 播放采样从约 39 秒推进至约 320 秒，readyState=4，缓冲持续领先约 70 秒 |
| 海外浏览器对照 | Chrome 的 example.com 实际连接为 VPNDirect / Windscribe |
| 完全退出 | 自有主内核与 helper 停止，代理还原记录清理，系统代理恢复原有关闭状态，VPN 接口保持在线 |
| 重开自动启用 | 无需点击启用即恢复组件与系统代理；UI 显示自动启用完成，重开后链路复核通过 |
| 启动偏好 | 登录启动已注册且未被 Windows 禁用；自动启用和关闭留在托盘的持久化偏好保持原值 |

本阶段未再次断开 VPN，不能把第一阶段的断线试验计为安装后的重复实测。浏览器采样只证明本次视频的实际路径和播放状态，不是所有视频、所有时段的性能保证。退出测试临时改变未保存的托盘行为；重开后偏好恢复，结束时保持分流启用。

## 本机证据

下列文件位于忽略的 out/，不随源码或安装包公开：

- isolated-routing/baseline.json、invalid-interface.json、cycle-reload.json：IKEv2 绑定、断线对照及同 PID 重载。
- isolated-routing/browser.json、browser-linked.json：浏览器视频与实际内核连接关联、逐连接代理还原。
- desktop-integration/report.json：默认与 IKEv2 RAS 原始值不同、部分外部改动的还原测试。
- desktop-integration/wireguard-20261111-092336.json：WireGuard 断线、自动恢复、内核恢复与代理冲突。
- desktop-integration/wireguard-20261111-092549.json：旧 IKEv2 配置自动跟随当前 WireGuard。
- desktop-integration/wireguard-20261111-094221.json：审查修复后的中断恢复、取消恢复保留会话、外部代理冲突及无关旧配置损坏回归。
- native-ui-v0.2.0/：原生界面截图；desktop-v0.2.0/：构建产物及 SHA-256。
- acceptance-v0.2.0/：安装文件比对、浏览器连接采样、退出前后及重开状态、出口复核、持久化偏好与验收摘要。原始运行记录不随发布上传。

中间部分失败样本伴随用户手动切换节点或协议，用户已确认，不能用它们证明 CLI 故障或代理回环；上述结论使用后续协议稳定的样本。

## 技术依据与边界

绑定出口及外部 DNS 均显式指定 VPNDirect，不设宽带回退。根据 [Mihomo direct 实现](https://github.com/MetaCubeX/mihomo/blob/v1.19.33/adapter/outbound/direct.go) 和 [Windows 接口绑定](https://github.com/MetaCubeX/mihomo/blob/v1.19.33/component/dialer/bind_windows.go)，接口绑定失败会返回错误。重载处理见 [executor](https://github.com/MetaCubeX/mihomo/blob/v1.19.33/hub/executor/executor.go)；接口自动识别使用 Windows [GetBestInterface](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getbestinterface)。这些源码结论与上述动态结果共同支持本次代理 TCP 场景，不能推广为整机防泄漏证明。

已验证本机从 0.1.1 安装升级后的运行和正常退出/重开。未覆盖全新机器安装、实际卸载、重启 Windows 后的真实登录启动、网络切换/休眠、Mullvad 本轮动态验证、UDP/TUN/IPv6、Windscribe 其他协议。Mullvad 保留既有 SOCKS5 路径并通过配置回归，不把此项写成动态验收。安装包未签名，0.2.0 按桌面预览版发布；正式上传产物从干净提交构建，提交来源以包内 build-info.json 和发布标签为准。
