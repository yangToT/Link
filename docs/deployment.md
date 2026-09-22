# 部署与维护

## Linux

要求 Linux x86_64、systemd、Python 3.9+、nftables 与 root 权限。当前实测 Rocky Linux 9.3。脚本不安装 Docker，不使用已有 MySQL 等数据库。

解压完整包，运行 `sudo sh deploy/install.sh 203.0.113.10`，将示例地址换成自己的公网 IPv4。部署目录固定为 `/ROOT/Link`；没有 `.link-owned` 标记的既有目录不会被覆盖。

| 方向 | 协议/端口 | 用途 |
| --- | --- | --- |
| 入站 | TCP 24443 | HTTPS 接入、gRPC 控制与中继 |
| 入站 | UDP 3478 | STUN |
| 出站 | TCP 443 | 下载与更新 |
| 出站 | UDP/TCP 53 | DNS |
| 仅专用网络 | TCP 24444 | 管理网页和 API，禁止公网放行 |
| 仅本机 | TCP 24445–24447 | 网络底层 API、健康和指标 |

有状态防火墙允许返回流量即可；无状态 ACL 还需要允许动态返回端口。入口设备使用 TCP 22000–22999 自动分配服务映射端口，不需要在云服务器安全组放行这一段。

上游部分内部监听会绑定所有地址；独立的 `link_guard` nftables 表阻止非回环访问这些端口。不能删除这张表后仍认为底层 API 是私有的。

实例生成自有 CA；加入码携带 CA SHA-256 指纹。客户端检查证书链、主机名、有效期和指纹，并安装该实例 CA 供组网组件和浏览器使用。

## 首次接入与恢复

首次管理员加入码位于 `/ROOT/Link/data/join-code.txt`，15 分钟内一次有效。后续加入码在管理网页生成。

管理员设备全部失联时，在服务器上运行：

```sh
/ROOT/Link/bin/link-server -data /ROOT/Link/data -command join -role admin
```

约 5 秒后运行中的服务读入恢复请求。命令不会覆盖设备状态文件。新码仍写到上述文件，不要放到公开 issue 或日志。

## Windows 客户端

完整包包含 `Link.exe`、`netbird.exe`、`wintun.dll` 和许可证。首次加入复制组件到 `%ProgramFiles%\Link`，注册 `LinkAgent` 服务；数据位于 `%ProgramData%\Link`。

设备令牌通过 DPAPI 加密，目录与命名管道仅系统和管理员可访问。此 Alpha 的界面也需要提升权限。关闭界面不停止后台；主动断开、被踢下线后不会自动重连。开机启动和自动连接分别设置。

关闭客户端后，以管理员身份运行 `uninstall.ps1` 卸载。默认保留身份；带 `-RemoveIdentity` 清除本机身份和对应 CA。脚本仅删除 Link 的服务、规则和已校验的目录。实际提升权限的安装和卸载路径仍需验收。

## 发布应用

先启动应用，在管理网页选择检测到的监听服务。应用应监听 `0.0.0.0` 或专用网络地址。转发的“可用”表示 TCP 可达，不表示业务健康。

对于需要公布地址的应用，在 IDEA 的 Run/Debug Configuration → Program arguments 保存生成的参数，然后正常启动和调试。应用继续负责注册和心跳。入口地址变化后需要更新参数。当前提供 Spring Cloud Nacos 地址参数示例，其他框架需各自验证适配器。

## Windows 服务端

```powershell
.\LinkServer.exe -data .\data -command init -public-host 203.0.113.10
.\LinkServer.exe -data .\data
```

EXE 包含控制服务与网页，不需要 Node 或 Java。但这不会自动部署 NetBird 服务端和专用管理节点。`data/config.json` 的底层地址、令牌、设备组和私有监听必须指向已经初始化好的环境。目前未提供完整的 Windows 本机组网服务端安装器，建议使用 Linux 完整包。

## 备份与维护

备份 `/ROOT/Link/data`，包含 SQLite、证书、私钥和身份。需要一致性备份时仅停止 `link-server`、`link-network`、`link-peer`，完成后恢复这三项，不涉及其他服务。升级前保存完整数据备份；安装脚本另保留旧控制程序到 `backups/`。

叶证书一年、底层管理令牌最长 365 天，尚无自动续期。证书通过 `-command certificate -hosts 公网IP,127.0.0.1,专用网络IP` 更新，然后仅重启 `link-server`；令牌轮换需要实例管理员维护。

仓库与程序包不包含真实实例地址、SSH 文件、加入码、设备令牌、数据库或私钥。
