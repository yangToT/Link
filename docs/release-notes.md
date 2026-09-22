首次 Alpha：原生 Windows 客户端、内嵌管理网页与自托管控制服务。

下载 Windows 客户端 ZIP，解压后运行 `Link.exe`。Linux 完整服务端包包含独立的网络组件，可部署到 `/ROOT/Link`。Windows 服务端 ZIP 提供 `LinkServer.exe`，但仅包含控制服务与网页，仍需另行提供组网底层；不是完整 Windows 单文件组网服务器。

已验证：Go 安全与状态测试、Windows 编译及真实本机 TCP 半关闭转发、Web 交互与窄屏布局、Linux 真实启动和公网管理隔离。

尚未验收：Windows 提升权限安装、自启、两台 Windows 跨网/RDP、实际资源访问与应用注册调用。重叠网段消歧、回环服务桥接、UDP 映射未实现。请将本版用于测试，不视为办公网络替代方案。

安装和维护步骤见包内 `docs/deployment.md`，详细边界见 `docs/validation.md`。自有代码 Apache-2.0；第三方组件保留各自许可证。程序尚未代码签名，校验值见 `SHA256SUMS.txt`。
