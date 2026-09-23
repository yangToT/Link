# 卸载与清理

Windows 客户端和 Windows 服务端包均提供 `Uninstall.exe` 与 `uninstall.ps1`；Linux 完整服务端包提供 `link-uninstall`、`deploy/uninstall.sh` 与 `deploy/uninstall.py`。Windows 执行程序弹出卸载确认并请求管理员权限；临时副本执行卸载，以免自身文件被占用。失败时显示记录路径并保留恢复材料。成功后的临时执行程序在下次系统启动时清除。

## Windows 客户端

双击安装目录的 `Uninstall.exe`，确认后断开网络并删除本机身份、配置、实例 CA、Link 服务与安装目录。可以先用只读计划查看范围：

```powershell
.\uninstall.ps1 -Plan
.\uninstall.ps1 -RemoveIdentity
```

脚本不带 `-RemoveIdentity` 时保留身份与配置。只有命令行脚本提供这种保留模式，双击执行程序默认完整移除。

按顺序禁用并停止 LinkAgent、停止安装目录内的 Link/NetBird 进程、重试二层资源日志清理、确认 Link0 已退出、停止和删除安装目录内的专属 SoftEther 服务、清理 Link 防火墙规则、删除自有程序与所选数据。清理失败不显示成功，不删除恢复日志，也不会通过重置全机网络解决问题。

Wintun DLL 的本产品副本会删除。共享的 Windows 驱动存储、用户已有 VPN 安装以及 v2rayN 配置不删除。当前 Link 尚未自动安装 SoftEther 驱动包，因此不能宣称存在可自动卸载的独占驱动安装记录；将来分发独占组件安装器时需记录包标识及共享引用，只有已证明不被其他软件使用的安装项才能删除。

## Linux 服务端

```sh
/ROOT/Link/link-uninstall --plan
/ROOT/Link/link-uninstall --yes
# 或使用脚本
sh /ROOT/Link/deploy/uninstall.sh --yes
# 保留身份、配置、数据库和备份
/ROOT/Link/link-uninstall --yes --keep-data
```

仅处理 `/ROOT/Link` 标记过的安装目录、指向其 deploy 目录的 Link systemd 单元和独立 `inet link_guard` 表。先停止组网、控制服务及专用管理节点，确认 Link0 已清理，再移除防火墙隔离和文件。不会卸载全局软件包、操作其他数据库或重置系统路由。客户端检测到控制服务失联后会断开和清理；无法把不在线的远端电脑瞬间卸载。

路径、服务归属、异常链接或剩余网卡存在问题时停止，保留现场。`--keep-data` 保留 data、backups 和归属标记，其余本产品文件删除。

## Windows 服务端

在便携包目录双击 `Uninstall.exe`，或执行：

```powershell
.\uninstall.ps1 -ProgramRoot $PWD -Plan
.\uninstall.ps1 -ProgramRoot $PWD -RemoveIdentity
```

仅结束可执行路径完全匹配该目录 `LinkServer.exe` 的进程/服务，删除包内程序和本目录有 `.link-owned` 标记的 data。其他文件保留。自定义外部数据目录、此前单独安装的 NetBird/SoftEther 服务端不归便携包卸载器管理。旧版没有数据归属标记时脚本拒绝自动删除数据，可先选择保留数据模式。

## 验证边界

源码测试在临时目录内验证清理顺序、外部服务拒绝、共享文件保留和失败后保留数据，系统命令使用替身。Windows/Linux 卸载程序已构建，但尚未在已部署实例执行破坏性卸载验收。当前服务器和本地客户端没有被卸载或更新。
