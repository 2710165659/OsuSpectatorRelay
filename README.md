# OsuSpectatorRelay

基于 Cloudflare Worker 的 **osu!lazer 旁观服务器加速项目**。配置完成后，通过快捷方式即可无感启动 osu 并在后台加速，兼容 VPN 的 Windows 系统代理模式：开启系统代理时使用 VPN，关闭时自动使用 Worker。

目前可以改善以下服务的访问：

- 每日挑战正常显示。
- 好友正常显示。
- 多人游戏房间列表正常显示。

多人进房和游玩仍存在稳定性问题，见下方“已知问题”。

## 使用教程

### 1. 编辑配置文件

打开与 `OsuSpectatorRelay.exe` 放在一起的 `config.json`，填写下面两项，其他设置先保持原样：

| 配置项 | 如何填写 |
| --- | --- |
| `osuPath` | osu 实际游戏 EXE 的完整路径，例如 `D:/Games/osu!/osu!.exe`。使用反斜杠时需写成 `D:\\Games\\osu!\\osu!.exe`；不要填写快捷方式或 Update.exe。 |
| `workerUrl` | Worker HTTPS 根地址，例如 `https://osu-relay.example.com`，不要加 `/health`、`/status` 或 `/tunnel`。 |

`sharedSecret` 已在构建发布包时随机生成，保留即可。配置文件缺失或密钥留空时，程序也会生成并保存密钥。路径、域名和密钥都读取此配置文件。

### 2. 在 Cloudflare 网页部署 Worker

1. 打开 Cloudflare 控制台的 **Workers & Pages**，新建 Worker。
2. 将发布包中 `worker/worker.js` 的完整代码复制到 Worker 编辑器并部署。
3. 在 Worker 设置中添加 **Secret 类型**变量 `SHARED_SECRET`，值填写本地 `config.json` 中的 `sharedSecret`，不要带两侧引号。
4. 将 compatibility date 设为 `2026-10-18` 或更新日期，保存并部署。
5. 确认本地 `workerUrl` 填写的是该 Worker 的可访问域名。

浏览器打开 Worker 根地址或 `/status`，例如 `https://osu-relay.example.com/status`。看到 `"status": "ok"` 和“Worker 运行正常，密钥已配置”表示部署可访问、服务端密钥已设置；显示 `configuration-required` 则需要配置 `SHARED_SECRET`。此公开页面不显示密钥，实际鉴权和隧道连接由下一步检查。

### 3. 检查连接并创建快捷方式

1. 双击 **`CheckWorker.cmd`**，检查密钥、HTTPS 和 WebSocket，并保存优选 IP；窗口提示 `Check passed` 表示通过。
2. 双击 **`CreateShortcut.cmd`**，在 **EXE 同级目录**生成 **osu! (Relay)** 快捷方式，图标与配置的 osu 程序一致。
3. 退出已经运行的 osu，再通过新快捷方式启动。后台代理自动启动，不弹命令行窗口。

启动前会预热实时服务线路，最长等待 `connectionBudgetSeconds`（默认 25 秒）；检查失败后仍会启动游戏。

### 4. 日常使用与更新

以后通过 **osu! (Relay)** 快捷方式启动即可，VPN 系统代理的开关会自动跟随；退出 osu 后，后台通常在 2–4 秒内退出。程序先复用上次优选 IP，后台复测期间也能使用；日志和状态保存在 `runtime` 文件夹。

更新时保留 `config.json` 和 `runtime`；修改配置后重新启动游戏。更新了 `worker.js` 时，也要在 Cloudflare 网页重新部署。

## 已知问题

- 有时连接不稳定，会发生断开。
- 多人游戏房间连接不稳定，进入房间后也可能在游玩过程中断开。

房间列表走网页 API，多人进房依赖实时连接，因此列表正常不代表一定能稳定进房。切换线路后若游戏仍无法恢复，可保持可用线路，正常退出 osu，再通过 Relay 快捷方式启动。后台重试与启动预热能降低连接失败的影响，但不能保证恢复游戏内部已停止重试的会话。

## 工作原理

启动器先启动隐藏的本地代理，预热实时服务线路，再给 osu 子进程设置代理地址。不修改 Windows 系统代理、hosts 或证书，也不安装开机启动项或系统服务。

| 场景 | 连接路线 |
| --- | --- |
| 系统代理关闭，访问匹配的 osu 服务域名 | osu → 本地代理 → 优选 CF IP / WSS → Worker → osu 官方 TCP 服务 |
| 系统代理开启 | osu → 本地代理 → 系统代理指定的 VPN → 官方服务；遵循 PAC 与绕过规则 |
| 系统代理关闭，访问其他主机 | 本地代理直连目标 |

系统代理连接失败时会重试。Worker 仅使用共享密钥鉴权，不检查来源域名，也不设置目标域名白名单。本地默认只精确分流 `spectator.osu.ppy.sh`，该域名同时承载旁观、多人和元数据服务。

内层 TLS 由 osu 与官方服务器完成，Worker 转发原始 TCP 字节。外层 WSS 使用 Worker 域名建立 TLS；优选 IP 只改变外层 TCP 入口，保留域名、SNI 和证书校验。

### 优选 IP 与失败恢复

候选网段与当前 ruleset 项目一致，但代码、配置和构建完全独立。默认从 25 个 IPv4 网段各随机抽取 2 个 IP，并加入历史优选 IP 和手动填写的 `preferredIps`；可选启用相同的 IPv6 候选网段。

测速分为以下步骤：

1. TCP 初筛：并发 16，超时 1200 ms，选取最快的 8 个候选。
2. HTTPS 测速：请求自有 Worker 的带密钥 `/health`，并发 6，超时 2500 ms；统计收到响应头的耗时，以 HTTP 耗时优先、TCP 耗时其次排序。
3. 复核：检查响应内容，再验证二进制 WebSocket `/probe`。复核不计入 HTTP 排名，最终使用通过复核的最快入口。固定 IP 均不可用时，尝试域名 DNS 入口。

HTTP 探测仅接受 2xx，403、重定向、验证码页面和错误响应都不可用。上次的结果会立即复用，启动及定期维护先检查现用 IP；首次检查失败后再确认一次，避免因瞬时波动换 IP。确认失效后重新完整测速；客户端到 Worker 的业务失败反复发生时，也会完整测速并优先尝试其他 IP。

Worker 明确报告上游错误且现用入口复测正常时，保留 CF IP。建连重试有次数、退避和总时间上限，已经转发的数据不会重放。系统代理开启时暂停正常后台 Worker 探测；双击检查工具仍会主动测试 Worker。

### 日志怎么看

日志保存在 `runtime/relay.log`，不记录密钥或隧道内容。

| 日志标记 | 含义 |
| --- | --- |
| `[游戏启动]`、`[启动判定]` | 游戏进程、路由方式，以及是否恢复历史优选 IP |
| `[优选 IP]` | 最终选择的 IP、HTTP/TCP 耗时；新连接将使用此入口 |
| `[业务连接]` | 实际流量的线路与所用 IP；同一路线的后续连接汇总记录 |
| `[连接汇总]` | 本地连接统计，约每 30 秒在数据有变化时输出；不是测速或心跳 |
| `[复测]`、`[测速开始]`、`[失败恢复]` | 检查原因、是否保留入口，以及重新测速和切换过程 |
| `[客户端→CF Worker]` | 外层连接、TLS、鉴权或 WebSocket 故障 |
| `[CF Worker→osu 服务器]` | Worker 明确报告的官方 TCP 上游故障；需部署新版 Worker 才能准确区分 |

同一域名上的旁观、多人和元数据会分别建连，因此有多个业务连接是正常现象。未收到明确错误报告时，程序会标出可观察到的故障位置或“无法确定”，不会把所有断线都当成 CF IP 失效。传输隧道建立也不等于游戏内部的 SignalR 会话已经就绪。

当前支持 Windows 系统代理模式，不检测 TUN 状态。网络或系统代理变化会关闭旧连接以触发重连，进行中的业务可能短暂中断；Worker TCP 出站仍受 Cloudflare 平台限制。

## 配置说明

日常只需设置 `osuPath`、`workerUrl`，并把 `sharedSecret` 同步到 Worker。

| 字段 | 默认值与作用 |
| --- | --- |
| `osuArguments` | 游戏启动参数数组，默认空 |
| `listenPort` | 17890，仅监听 IPv4 回环地址 |
| `workerHosts` | 精确主机名数组，默认仅 spectator.osu.ppy.sh |
| `routeAllThroughWorker` | false，保留精确分流 |
| `autoGenerateCandidates` | true，按候选网段随机抽样 |
| `enableIpv6Candidates` | false，启用后同时抽样 IPv6 候选 |
| `preferredIps` | 额外候选 IP 数组，最多 32 个，默认空；历史优选 IP 自动加入 |
| `probeIntervalSeconds` | 300，现用入口定期复测间隔 |
| `connectTimeoutSeconds` | 8，单次业务建连超时；TCP/HTTP 初筛使用上述固定超时 |
| `retryAttempts` | 3，单次建连最大尝试次数 |
| `retryDelayMilliseconds` | 500，初始退避延迟，后续递增并带随机抖动 |
| `connectionBudgetSeconds` | 25，单次业务建连总时间上限 |
| `idleExitSeconds` | 180，未注册游戏时的后台空闲回收时间；游戏退出通常在 2–4 秒内退出 |
| `enableWpad` | false，避免把默认“自动检测设置”当成 VPN 已开启 |
| `checkBeforeLaunch` | true，启动前预热官方实时服务线路，失败后仍启动游戏 |

历史优选结果保存在 `runtime/preferred-ip.json`，更换 Worker 地址后会重新探测。

## 开发说明

项目使用 C# / .NET 8 和原生 JavaScript，不引用 osu 或 ruleset 的源码文件、程序集，也没有第三方 NuGet 依赖。

安装 .NET 8 SDK 后，在 PowerShell 构建：

```powershell
& 'E:\MyCodes\c#\OsuSpectatorRelay\build.ps1'
```

默认覆盖 `artifacts/win-x64` 的程序及附带文件，保留已有 `config.json` 和运行数据。发布使用运行库裁剪和单文件压缩，仍自带运行时，可直接运行。ARM64 使用 `-Runtime win-arm64`；依赖系统 .NET 的发布使用 `-FrameworkDependent`。

安装 Node.js 后可运行完整测试：

```powershell
& 'E:\MyCodes\c#\OsuSpectatorRelay\test.ps1'
```

测试使用本机临时端口、测试证书、模拟上游及专用测试进程，覆盖分流、重试、HTTP 探测、WebSocket 转发、缓存、IPC 和游戏退出回收。真实 Cloudflare 部署及游戏内连接仍需实机验证。
