# Link

自托管的设备网络与服务访问工具。Windows 客户端连接自己的实例，通过专用网络访问设备和资源。

**客户端 0.1.0-alpha.2 / 服务端 0.1.0-alpha.1，测试版本。** Linux 服务端和一台真实 Windows 客户端已连接，私有管理登录与后台服务重启恢复通过验证。两台 Windows 跨网互通、系统重启后恢复和实际应用调用尚未验收。

| 程序 | 内容 |
| --- | --- |
| `Link.exe` | Windows WPF 界面、独立后台服务、设备连接、远程桌面快捷入口、网络入口标识和 TCP 映射 |
| `link-server` | Linux 控制服务与内嵌网页；完整包包含独立运行的 NetBird 网络组件 |
| `LinkServer.exe` | Windows 控制服务与内嵌网页；**不是完整的 Windows 单文件组网服务器**，需要另行提供 NetBird 服务端。完整部署脚本目前仅支持 Linux |

## 使用

1. Linux 服务器放行入站 **TCP 24443、UDP 3478**。安装下载需要出站 TCP 443 与 DNS。管理端口不对公网开放。
2. 解压 Linux 完整包，运行 `sudo sh deploy/install.sh 公网IPv4`。文件统一安装到 `/ROOT/Link`，仅管理本产品的 `link-*` 服务。
3. 首次管理员加入码保存于 `/ROOT/Link/data/join-code.txt`，15 分钟内一次有效，不依赖先访问私有网页。
4. Windows 解压完整客户端包，以管理员身份启动 `Link.exe`，输入 `https://公网IPv4:24443` 和加入码。
5. 管理员连接成功后通过客户端打开管理中心，添加设备、设置网络入口和服务映射。

Windows 客户端最小化或关闭窗口后收起到右下角托盘；双击托盘图标或再次启动 `Link.exe` 可恢复已有窗口。右键菜单提供管理中心入口和“退出界面（保持连接）”。Windows 可能将图标放在托盘的 `^` 隐藏区域，可将其拖到常显区域。

程序下载见 [GitHub Releases](https://github.com/yangToT/Link/releases)。请先阅读对应版本的验证记录。

## 当前边界

- 客户端面向 Windows 10/11 x64、.NET Framework 4.8，需要管理员权限，尚未代码签名。
- RDP 目标必须支持并启用 Windows 远程桌面，仍使用原 Windows 账号。不会绕过系统认证。
- 网络底层使用 NetBird 0.79.0。点对点失败时尝试中继，实际速度受两端和服务器带宽影响。
- 网络入口发现私有 IPv4 路由；**重叠网段自动消歧未实现**。
- 服务映射目前为 TCP。仅绑定 `127.0.0.1` 的应用暂不支持自动桥接。
- 应用自行维护注册和心跳。入口地址变化后需更新生成的启动参数。
- IPv6、私有 DNS 域管理、自动升级与证书/API 令牌自动续期尚未实现。

## 开发

Windows 安装 Go 1.24+，运行 `./build.ps1`。客户端使用 Windows 的 .NET Framework 编译器，不需要额外 .NET SDK。

```powershell
./build.ps1
./client/build.ps1 -Check
./artifacts/client-windows-amd64/Link.Check.exe --self-test
```

`server/web/` 是内嵌 HTML/CSS/JavaScript，无独立前端服务。`node server/web/check.cjs` 运行 Playwright 界面检查，需要 Chrome；`PLAYWRIGHT_MODULE` 可指定模块路径。该检查使用合成 API，不等于组网验收。

## 文档与许可

- [部署与恢复](docs/deployment.md)
- [验证记录](docs/validation.md)
- [需求基线](docs/design.md)
- [第三方声明](THIRD-PARTY-NOTICES.md)

自有代码采用 [Apache-2.0](LICENSE)。独立网络组件和驱动保留各自许可证，完整分发包不统一改为 Apache-2.0。
