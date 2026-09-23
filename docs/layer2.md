# 二层局域网接入（开发预览）

此变更在现有专用网络上增加可选的以太网接入。成员设备获得入口所在局域网的地址，局域网中的其他设备可按该地址访问成员。服务不再逐端口映射，也不由 Link 改写某个 IDE 或业务框架的配置。

**这是默认关闭的实验功能，尚未完成两台 Windows 的真实组网验收。** 0.2.0-alpha.2 包含集成代码及组件准备脚本，不包含 SoftEther 二进制组件。组件准备脚本使用官方稳定版 4.44 Build 9807；Windows 载荷必须通过 SoftEther Corporation 的 Authenticode 签名校验。当前尚无包含这些组件的完整发布包。

## 结构

- NetBird 继续负责设备身份、专用地址、控制通道和管理网页访问。
- 可选 SoftEther Server 提供独立虚拟 HUB，以专用地址承载以太网数据；不桥接云服务器物理网卡。
- 唯一的有线入口通过 SoftEther Bridge 连接 HUB，并桥接所选物理以太网卡。
- 成员通过 SoftEther Client 的独立虚拟网卡连接 HUB，由入口网络的 DHCP 分配地址。
- Link 控制账号授权、角色策略、接入配置、撤销与恢复记录。SoftEther 管理员凭据留在各自主机，不发给其他设备。

## 管理员准备

Windows 客户端打开 **功能与组件 → 局域网接入**，点击“安装组件”。程序下载固定哈希的官方安装包，验证发布者签名，从包中提取签名载荷并完成本机配置。界面正常打开无需管理员权限；首次安装基础组件、安装/修复/卸载可选组件时由 Windows 请求提权。后台管道仅授权安装账户与系统管理员，私有身份文件仍只有 SYSTEM 和管理员能读取。标准账户使用另一管理员凭据首次安装时，当前授权账户为执行安装的管理员；暂不支持多 Windows 用户分别授权。

安装完成默认关闭。点击“启用”表示本机同意参与；还需要管理端选择已启用且组件就绪的有线入口，并启用局域网模式。成员未安装或未启用时不发放二层凭据，基础 Link 连接继续工作。点击“停用”先清理本机二层连接与自有网络规则；“修复”重新准备组件并保持停用；“卸载组件”不会删除设备身份、基础后台服务或 NetBird。入口停用会使依赖它的局域网访问中断，服务端停止签发并周期性撤销对应授权。旧客户端未报告主动启用状态时，视为未启用。

下面的脚本用于服务器部署和排障，普通客户端用户无需手工执行。

已准备并核验 Linux 二进制后，可运行 `python3 /ROOT/Link/deploy/configure-layer2.py`。脚本只接受 Link 专属目录，先配置端口隔离，再启动 `link-layer2`、验证真实管理接口并更新控制配置；只重启 `link-server`，保留原接入模式。安装失败保存现场并回退控制配置与防火墙规则。服务初始密码使用 SoftEther 配置格式要求的 SHA-0，不能使用 SHA-1 代替；API 请求仍通过 HTTPS 传递独立随机密码。

Windows 可对已从官方签名安装包提取的 `client-portable/`、`bridge-portable/` 目录运行 `client/install-layer2.ps1 -ComponentSource <目录>`。它校验签名、拒绝已有共享组件、保存 DPAPI 凭据并验证 Client/Bridge 本机管理接口，不创建成员虚拟网卡或办公网卡桥接。组件使用独立目录及有归属记录的防火墙规则。该脚本不等同于驱动与跨机接入验收。

本机组件管理采用非交互命令及超时限制，Bridge 使用服务管理员认证后选择 `BRIDGE`，命令输入文件使用无 BOM 的 UTF-8。准备后可用管理员权限运行 `Link.exe --layer2-check`；失败会返回非零退出码并在程序目录生成 `startup-error.txt`，不会启动连接。

SoftEther 启动时可能安装 SeLow 抓包协议驱动并绑定网卡，这与创建以太网桥接不同。安装脚本记录安装前后的驱动和驱动包清单。卸载只清理本次新增的 SeLow；发现其他 SoftEther 服务时保留共享驱动，驱动包需与记录中的名称、路径、提供方、版本一致才删除。使用 Windows 的 [netcfg /u](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netcfg) 和 [PnPUtil /delete-driver](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)，不执行全网卡重置、强制删包或自动重启。清理失败保留安装目录和恢复记录。

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
- 只在 Link 创建且校验归属的成员虚拟网卡上移除默认路由、沿用本地 DNS，禁用 IPv6 和 DNS 注册。DHCP 地址保留；Windows 可能将 DHCP 默认路由标为 NetMgmt，也按网卡归属清理。私有目标路由绑定该虚拟网卡；不重写物理网卡 DNS、默认网关或代理配置。
- 必要时创建到 Link 公网控制端点的物理出口 `/32` 路由，并记录归属；已有同目的路由不接管。检查云端专用地址经 Link0 走、入口网关经成员虚拟网卡走。
- 重叠网段、路由被 TUN 抢占、异常 DHCP 路由、无法隔离 DNS 等情况会停止接入并显示原因。路由检查不能证明 v2rayN 的 WFP/严格路由规则一定放行；双端 TUN 共存仍需实际验证。
- 停用、踢下线、角色变更撤销对应 SoftEther 用户访问和现有会话。授权账号续期不周期性强断正常连接。90 秒账号到期限制后续认证，**不会强制终止已有会话**；失联设备由服务端定时撤销。管理接口不可用时无法保证立即撤销已有会话，此情况必须按故障处理。
- 主动断开、服务停止、启动恢复只清理随机命名且已记录归属的账号、桥接、虚拟网卡和出口路由。失败保留恢复记录，后台继续重试；卸载在清理失败时保留安装目录和恢复数据。

## 无法由二层接入消除的条件

入口交换机需要允许额外 MAC，DHCP 需要允许给远端设备发地址。端口安全、802.1X 或 DHCP 白名单可能阻止这一点。只操作入口电脑无法绕过这些网络准入条件。

应用需要监听可达网卡，Windows 防火墙需要允许相应入站访问。当前代码不自动放开成员所有入站端口。只监听 `127.0.0.1` 或主动公布其他网卡地址的应用，仍可能需要调整自身设置；不能承诺任意应用在多网卡环境下都能零配置注册。拥有局域网地址和业务请求成功是两项不同验收。

## 验证

SoftEther Stable 4.44/9807 的 `AccountDetailSet` 不支持 `/DISABLEUDP`。客户端 alpha.9 已移除此参数；TCP 传输由专用 Hub 的 `DisableUdpAcceleration` 选项保证。新部署准备脚本自动设置。已部署的 Linux 实例须先将本版 `deploy/layer2_transport.py` 放到 `/ROOT/Link/deploy/`，运行 `sudo python3 /ROOT/Link/deploy/layer2_transport.py`：仅调整 Link 的 Hub、保留其他选项并回读验证，无需重启服务。不应直接改写正在运行的 SoftEther 配置文件。

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

alpha.10 对专用路由暂时重连提供最长 30 秒恢复等待，期间保留已有网卡，超时清理。服务端返回独立的等待状态且不续发凭据，身份撤销仍立即进入清理。最近失败原因随复制诊断输出。

alpha.11 将最长 30 秒恢复等待扩展到取址后的局域网路由，等待期间不重建网卡。界面和复制诊断报告实际/期望网卡及路由；没有证据时不归因于代理 TUN。持续冲突依然清理，不会改写其他网卡的路由优先级。
