# 二层局域网接入（开发预览）

此变更在现有专用网络上增加可选的以太网接入。成员设备获得入口所在局域网的地址，局域网中的其他设备可按该地址访问成员。服务不再逐端口映射，也不由 Link 改写某个 IDE 或业务框架的配置。

**这是默认关闭的实验功能，尚未启用或完成两台 Windows 的真实组网验收。** 0.2.0-alpha.1 包含集成代码，不包含 SoftEther 组件。当前不提供自动安装 SoftEther 驱动和组件的完整分发包；组件版本、签名、许可证、安装和卸载流程验证是分发这些组件前的必做项。

## 结构

- NetBird 继续负责设备身份、专用地址、控制通道和管理网页访问。
- 可选 SoftEther Server 提供独立虚拟 HUB，以专用地址承载以太网数据；不桥接云服务器物理网卡。
- 唯一的有线入口通过 SoftEther Bridge 连接 HUB，并桥接所选物理以太网卡。
- 成员通过 SoftEther Client 的独立虚拟网卡连接 HUB，由入口网络的 DHCP 分配地址。
- Link 控制账号授权、角色策略、接入配置、撤销与恢复记录。SoftEther 管理员凭据留在各自主机，不发给其他设备。

## 管理员准备

服务端在 `/ROOT/Link` 下另设 `softether/`，准备支持官方 JSON-RPC 的 SoftEther Server。关闭 SecureNAT、虚拟 DHCP、VPN Azure、动态 DNS、IPsec、OpenVPN、SSTP 等不使用的功能，不复用已有 VPN HUB 或账号。单独建立 `LINK` HUB，设置至少 20 字符的随机管理密码和独立服务器证书。上游版本和二进制哈希在真实部署前固定；不能将“调用接口已编译”视为组件兼容性验收。

在 Link 服务端配置增加 `layer2` 对象：

```json
{
  "layer2": {
    "endpoint": "100.88.0.1:24448",
    "apiUrl": "https://127.0.0.1:24448/api/",
    "hub": "LINK",
    "password": "<独立随机管理密码>",
    "certificate": "<该组件的完整 PEM 证书>"
  }
}
```

示例地址必须替换为服务器实际专用地址。证书使用完整叶证书固定校验；接口限定 HTTPS 回环地址，不跟随重定向。包含凭据的配置保持仅服务账号可读，不进入仓库。

新增 **TCP 24448 只允许回环和 Link 专用接口访问**，不是公网入站端口。云主机防火墙需先加入该限制；SoftEther 默认通配监听不能单靠示例地址实现隔离。现有公网 TCP 24443 / UDP 3478 不变。Windows 组件仅需通过 Link 专用网络出站访问 TCP 24448；本机 Client 管理接口和 Bridge TCP 5555 仅允许回环。禁止放开这些管理端口到公网或局域网。

Windows 组件安装到 `%ProgramFiles%\Link\softether\`，使用专属的 `SEVPNCLIENT` / `SEVPNBRIDGE` 服务（开发版服务名带 `DEV` 后缀，也会识别；同角色安装多个版本时拒绝操作）。发现服务来自其他路径时 Link 拒绝操作。成员需要 Client，入口需要 Bridge；如需切换角色，应同时准备两者。入口 Bridge 保留专用 `BRIDGE` HUB，关闭不用的监听和协议，设置独立管理密码。不要移除办公网卡现有 TCP/IP 绑定或创建 Windows 系统“网络桥”。

已安装组件后，用管理员 PowerShell 保存本机凭据，例如：

```powershell
$clientSecret = Read-Host '本机 Client 管理密码' -AsSecureString
$bridgeSecret = Read-Host '本机 Bridge 管理密码' -AsSecureString
.\client\prepare-layer2.ps1 -VpnCmd 'C:\Program Files\Link\softether\vpncmd.exe' -ClientPassword $clientSecret -BridgePassword $bridgeSecret
```

此脚本只保存 DPAPI 配置，不安装组件、不更改组件密码、不启动连接。仅有一种角色时只传对应密码。设备隧道凭据通过仅 SYSTEM/管理员可读的临时输入文件交给 vpncmd，随后删除；**本机组件管理密码仍需通过 vpncmd 的命令行参数传入**，本地管理员可观察该进程。本机管理员属于信任边界，不能宣称完全无明文内存或进程参数暴露。

两端升级后，管理中心先选择报告正常的有线入口，移除原端口映射，再启用二层接入。缺少组件配置、入口不可用或旧路由撤销失败时不会切换成功。切回路由模式后需重新选择入口，避免静默恢复过期路由。

## 网络与代理共存

- 从硬件清单和网卡 GUID 识别物理网卡，排除 TUN/WireGuard/SoftEther/蓝牙等网卡。有多块有线网卡时要求显式选择；已选择网卡掉线时不会自动桥接另一张网卡。
- 入口目前只接受有线以太网。Wi-Fi 可供成员普通联网，但不能作为二层入口；由网线切到 Wi-Fi 会中止二层接入。
- 只在 Link 创建的成员虚拟网卡上禁用默认路由、DNS 接管、IPv6 和 DNS 注册。私有目标路由绑定该虚拟网卡；不重写物理网卡 DNS、默认网关或代理配置。
- 必要时创建到 Link 公网控制端点的物理出口 `/32` 路由，并记录归属；已有同目的路由不接管。检查云端专用地址经 Link0 走、入口网关经成员虚拟网卡走。
- 重叠网段、路由被 TUN 抢占、异常 DHCP 路由、无法隔离 DNS 等情况会停止接入并显示原因。路由检查不能证明 v2rayN 的 WFP/严格路由规则一定放行；双端 TUN 共存仍需实际验证。
- 停用、踢下线、角色变更撤销对应 SoftEther 用户访问和现有会话。授权账号续期不周期性强断正常连接。90 秒账号到期限制后续认证，**不会强制终止已有会话**；失联设备由服务端定时撤销。管理接口不可用时无法保证立即撤销已有会话，此情况必须按故障处理。
- 主动断开、服务停止、启动恢复只清理随机命名且已记录归属的账号、桥接、虚拟网卡和出口路由。失败保留恢复记录，后台继续重试；卸载在清理失败时保留安装目录和恢复数据。

## 无法由二层接入消除的条件

入口交换机需要允许额外 MAC，DHCP 需要允许给远端设备发地址。端口安全、802.1X 或 DHCP 白名单可能阻止这一点。只操作入口电脑无法绕过这些网络准入条件。

应用需要监听可达网卡，Windows 防火墙需要允许相应入站访问。当前代码不自动放开成员所有入站端口。只监听 `127.0.0.1` 或主动公布其他网卡地址的应用，仍可能需要调整自身设置；不能承诺任意应用在多网卡环境下都能零配置注册。拥有局域网地址和业务请求成功是两项不同验收。

## 验证

本地检查：Go 测试覆盖 TLS 固定校验、授权撤销、模式切换约束和 TUN 报告过滤；C# 自检覆盖物理网卡选择、网段冲突、断开失败后的日志恢复；PowerShell 检查使用 cmdlet 替身验证路由归属，不修改宿主机网络。网页测试使用合成 API。

```powershell
.\build.ps1
.\client\build.ps1 -Check
.\artifacts\client-windows-amd64\Link.Check.exe --self-test
powershell.exe -NoProfile -File .\client\test-layer2-network.ps1
node .\server\web\check.cjs
```

上线前逐项验收：有线入口加成员 DHCP；无 Link 的局域网电脑访问成员 TCP/UDP 服务；成员访问不同内网子网；双方 TUN 开启及重启；正常互联网和代理出口；mstsc；应用注册和真实调用；入口切网、异常退出、断电恢复；踢下线/停用；断开前后路由、DNS、网关及代理规则对比。该清单目前未完成真实跨机验证。

上游依据：[本地桥接](https://www.softether.org/4-docs/1-manual/3/3.6)、[JSON-RPC 接口](https://github.com/SoftEtherVPN/SoftEtherVPN/blob/master/developer_tools/vpnserver-jsonrpc-clients/README.md)、[Windows TUN 严格路由](https://sing-box.sagernet.org/configuration/inbound/tun/)。
