# 源码与构建

本文对应无线网络共享 1.9.0。使用流程见仓库根目录 [README](../README.md)，离线测试见 [tests/README.md](../tests/README.md)。

## 三个独立项目

`WirelessInternet.sln` 使用传统 MSBuild 工程，目标为 .NET Framework 4.8、Windows x64、C# 5，不依赖 NuGet 还原。使用已安装的 Visual Studio Build Tools / MSBuild 和 .NET Framework 4.8 targeting pack；构建过程不会下载或安装开发工具。普通用户运行现成程序无需安装这些开发工具。

| 项目 | 源文件 | 目标 |
| --- | --- | --- |
| `WirelessInternet.csproj` | `Program`、`Backend`、`WorkerActionClient`、`ActionProtocol`、监控组件、日志视图与自绘控件、`MemoryLog`、`AppForm`、`SetupForm`、`SetupBackend`、`AndroidProtocol`、`UiDpi`、`ProcessExecution`、`SessionStatus`、`UiOperationCoordinator` | `WirelessInternet.exe`，WinExe |
| `ConnectionWorker.csproj` | `WorkerHost`、`ActionProtocol`、`RuntimeCommon`、`RuntimeSessionStore`、`OwnedProcessManager`、`AndroidDeviceClient`、`AndroidProtocol`、`NativeNetwork`、`ConnectionController`、`ConnectionOperation`、`WatchdogController`、`PhoneGuardSource`、`ProcessExecution` | `ConnectionWorker.exe`，Exe |
| `RelayLogHost.csproj` | `RelayLogHost`、`MemoryLog` | `RelayLogHost.exe`，WinExe |

程序集引用和源清单只在项目文件中维护。共享源码分别编入需要它的组件，保持桌面、连接/保护 worker 和日志 host 的现有进程边界，不增加运行时共享 DLL。

GUI 和 worker 同时编入 `SharedDataModels.cs` 与 `ConfigurationDocument.cs`。它们不依赖 WMI、设备命令或文件 I/O；网络发现仍由 `NativeNetwork` 执行，持久化和校验仍在各调用方。

每个项目使用独立的 `obj/<项目>/<配置>/`。默认输出统一在源码目录的 `bin/`；可通过 `OutDir` 指定隔离输出目录。三个项目都将本地 `App.config` 复制为对应的 `.exe.config`。

## 隔离编译

在仓库根目录打开开发者 PowerShell；若 `MSBuild.exe` 不在 PATH，请使用本机实际的 MSBuild 完整路径。下面仅创建独立临时输出，不启动应用或手机连接，也不覆盖当前可执行文件。

```powershell
$msbuild = (Get-Command MSBuild.exe -ErrorAction Stop).Source
$out = Join-Path ([IO.Path]::GetTempPath()) ('WirelessInternet-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
& $msbuild .\source\WirelessInternet.sln /nologo /m:1 /nr:false /t:Build /v:minimal `
    /p:Configuration=Release /p:Platform=x64 /p:UseSharedCompilation=false "/p:OutDir=$out\"
if ($LASTEXITCODE -ne 0) { throw 'MSBuild 编译失败。' }
Write-Output $out
```

`Debug|x64` 也已定义。调试符号只用于开发，不放入最终运行包。桌面项目保留源文件声明的程序集版本和框架属性，避免 MSBuild 重复生成属性。

编译结果只有三个 EXE 和相邻运行配置，并非完整的共享目录。实际运行还需将 worker 和日志 host 放入 `support/`，保留 ADB、gnirehtet relay/APK、图标、未绑定配置模板与第三方许可。不要将编译结果覆盖到仍有共享会话或后台进程的目录。

## 连接生命周期与职责

`ConnectionController` 保留开始、断开与检查的编排职责。旧 `Start`／`Stop` 入口仍返回退出码；`StartWithResult`／`StopWithResult` 及 `LastResult` 提供操作种类、最终阶段、退出码、消息和只读阶段序列。`ConnectionOperation` 定义这些结果与操作跟踪结构，控制台通过原有输出通道显示中文阶段进度及失败阶段。

实际执行顺序、状态提交点和失败后保留或清理资源的规则沿用原协议。本次启动创建的资源通过操作记录追踪，失败后按归属和提交状态处理；结构化结果本身描述一次已结束的操作，并非实时共享会话。

| 组件 | 职责与边界 |
| --- | --- |
| `AndroidDeviceClient` | worker 的手机发现与身份、系统开关及 VPN 限制检查；启动阶段仍保留原重试策略 |
| `AndroidProtocol` | 无 I/O 的 mDNS、IPv4 端点、VPN、反向隧道及守护回复解析；Setup 人工端口验证与 Watchdog 恢复策略由各调用方保留 |
| `RuntimeSessionStore` | 配置读取、会话校验与原子保存、未知字段保留、按 token 删除；调用者负责原有操作锁 |
| `OwnedProcessManager` | 按 PID、启动时间和准确路径核验进程，固定进程句柄后操作；保留后台组件的标准句柄隔离 |
| `RuntimeContext` | 兼容门面，保留现有虚方法和调用入口并转发到服务，使注入测试及原调用方能逐步迁移 |

核心互斥锁的名称、获取／释放时机及等待策略保持不变。手机控制与会话提交仍在原有锁范围执行；等待首次监控确认前按原协议释放锁，再通过会话与进程身份重新核验。会话记录按 token 删除，已跟踪进程按 PID、启动时间和路径核验后清理；已提交会话的必要资源按原恢复路径保留，不按进程名称或进程树清理。

## 操作结果通道

`ActionProtocol` 提供 GUI／worker 共用的阶段类型、中文显示名称和有长度上限的版本化帧。每次开始／断开生成独立随机操作 ID 与本机命名管道；worker 在执行前建立通道，阶段通知由后台写入器合并发送，最终结果包含最终阶段、退出码、消息与完整阶段序列。控制台仍保留可限流的文字输出，结果不依赖控制台缓存或 UI 日志队列。

`WorkerActionClient` 独立读取并核对帧版本、操作 ID、动作与格式，收到最终结果后还须核对真实进程退出码。`Backend.RunActionWithResult` 返回 `WorkerActionResult`；原 `RunAction` 入口保留，但缺失、损坏或不匹配的结果不会映射为成功。界面阶段回调异步派发，旧操作的迟到阶段不能覆盖最终结果或下一次操作。失败阶段与“结果未确认”短暂保留在状态区，并写入内存控制台。

结果通道在动作开始前无法连接时不执行连接／断开副作用。动作开始后，界面退出或不读取管道不会取消部分完成的清理，worker 继续执行原有安全顺序；通道写入与退出排空等待有上限，连接操作本身沿用原有等待规则，不增加外层强杀超时。此通道不改变会话文件格式、保护心跳或操作锁。

## 共享配置与会话数据

`SharedDataModels` 集中定义纯网络数据 `NetworkLink` 和共有会话字段 `SessionRecord`。worker 的 `RuntimeState` 继承这些字段，仅补充控制和中继宿主字段及不参与 JSON 序列化的原始字典。网络成本继续使用 `long`／`long?`；worker 新会话的保护时间仍默认为 30，GUI 缺失该字段时仍保留原来的 0。

GUI 的观察字段通过属性标记，解码时只投影原来的 14 个成员，再执行原有 token、身份和端点形状校验。这样新增的网络字段不会让 GUI 开始拒绝原本忽略的附加内容。worker 继续完整反序列化并调用原严格校验和虚方法回调，不因共用数据类型而放宽控制条件。

`ConfigurationDocument` 包装原始配置字典，集中六个常用字段、JSON 编解码与基础转换。读取属性不会插入缺省键；未识别的嵌套字段和值保持原样。worker 使用严格整数转换和超时范围校验，绑定界面使用原有索引回退规则；`NativeNetwork` 的大小写、重复键及网卡选择规则独立保留。只有向导确认保存时才明确更新绑定字段、保护时间和旧 alias；文件原子替换、大小限制、异常消息与互斥协议留在原位置。

## 显示与运行配置

1.7.0 将桌面监控与渲染拆为独立组件，不改变后台连接、锁与手机守护协议：

| 组件 | 职责 |
| --- | --- |
| `MonitoringModels` | 沿用原有快照、观察源接口与采样策略；仅为 GUI 内部源码提取，不更改会话格式或进程通信 |
| `SystemSessionObservationSource` | 只读会话文件、进程归属、TCP 表、手机计数及内存日志；保持原采样命令和前后验证 |
| `PhoneTrafficTracker` | 无系统 I/O 的累计流量、速度基线、节流、缓存、暂停与恢复状态 |
| `MonitorSnapshotAssembler` | 组合会话健康、连接、日志和统计快照，维护原有观察顺序 |
| `ConsoleLogView` | 有界日志队列、去重、RTF 批次、选择与滚动保留及清空；后台入队不访问控件 |
| `DashboardControls`／`UiVisualStyle` | 独立自绘控件与不可变视觉值，沿用原布局、字体、颜色、DPI 和图表语义 |

`Backend` 保留原公开入口作为兼容门面。`AppForm` 继续负责布局和用户事件；操作准入、退出和采样互斥仍只由 `UiOperationCoordinator` 管理。日志视图只拥有渲染状态，不另建连接或退出状态。

日志上限及调度保持原值：待显示消息最多 256 条／65536 字符，单批最多 64 条／12288 字符，显示最多 1500 行／200000 字符，单条最多 4096 字符，UI 计时器仍为 120ms。隐藏窗口或连接列表页暂停文字绘制，恢复后保留阅读位置；日志不默认写入文件。

GUI 与 worker 使用 `app.manifest`，保留 `dpiAware=true` 的 SystemAware 声明；`.config` 只声明 .NET Framework 4.8。各运行配置须与其 EXE 相邻。

桌面图标优先从 `../support/wireless.ico` 读取；工作区图标位于 `bin/wireless.ico` 时作为次选。两处均无图标时不影响编译。跨倍率显示器仍由 Windows 整窗缩放，可能插值；本轮没有迁移 UI 框架或更改为 PerMonitorV2。

## 采样与保护调度

继续保留 1.5.1 的采样策略：`PhoneSamplingPolicy` 默认每 3 秒采样，缓存速度只在采样年龄不超过 4 秒时有效。主窗口显示且未最小化时采样，切到 TCP 连接页仍会采样。主窗口最小化或收起到托盘后不再发起新的非安全手机统计查询，已在途的查询允许完成；恢复显示时立即采样并重建速度基线，到下一个完整采样间隔后才显示速度。正常节流不记作采样失败，也不重复添加曲线点。速度为最近采样间隔的平均值，累计流量仍来自手机 VPN 接口计数，不能将暂停采样理解为暂停传输。

独立 worker 的约 5 秒保护检查、手机会话心跳及默认 30 秒失联阈值保持不变。手机守护用 shell 内建 `read`／`printf` 替代小文件读取中的外部 `cat`，活性标记 `watch_tick` 约每 5 秒原子写入一次，写入失败下轮重试。手机每秒检查会话／心跳期限、原有 10 秒守护活性阈值保持不变；这些保护仍按会话运行，不依赖界面统计定时器。此前的命令和写入计数不等于已经测得的耗电降幅；1.6.0 没有调整这些保护与采样参数。

## 打包边界

发布入口为 `packaging/Run-Release.ps1`：默认快速检查 → 三个项目 Release 构建和安装包 → 隔离重编包内源码 → 比较三个 EXE／三个 `.exe.config` 的 SHA-256。输入来源、报告及历史回归选项见 [发布说明](../packaging/README.md)。GitHub Actions 使用同一入口，手机和 DPI 保持手工验收。

打包使用显式文件清单，仅发布未绑定的默认配置，保留第三方 NOTICE/LICENSE 原文。README、LICENSE、源码 BUILD.md 和自写 THIRD-PARTY.md 随运行包提供。

电脑端运行不依赖 PowerShell；工作区 `build.ps1` 只是调用 solution 并生成开发图标的辅助工具，不是运行依赖，不放进安装包。手机守护文本仍内置于 C#，按会话临时传输。

本机 `wireless-config.json`、活动 `state/`、调试输出、真实网络报告、ADB 凭据及备份不属于源码或发布输入。网络检查和进程清理的保护边界不因工程文件拆分而改变。
