# 源码与构建

本文对应无线网络共享 1.5.1。使用流程见仓库根目录 [README](../README.md)，离线回归见 [tests/README.md](../tests/README.md)。

## 三个独立项目

`WirelessInternet.sln` 使用传统 MSBuild 工程，目标为 .NET Framework 4.8、Windows x64、C# 5，不依赖 NuGet 还原。使用已安装的 Visual Studio Build Tools / MSBuild 和 .NET Framework 4.8 targeting pack；构建过程不会下载或安装开发工具。普通用户运行现成程序无需安装这些开发工具。

| 项目 | 源文件 | 目标 |
| --- | --- | --- |
| `WirelessInternet.csproj` | `Program`、`Backend`、`MemoryLog`、`AppForm`、`SetupForm`、`SetupBackend`、`UiDpi`、`ProcessExecution`、`SessionStatus`、`UiOperationCoordinator` | `WirelessInternet.exe`，WinExe |
| `ConnectionWorker.csproj` | `WorkerHost`、`RuntimeCommon`、`NativeNetwork`、`ConnectionController`、`WatchdogController`、`PhoneGuardSource`、`ProcessExecution` | `ConnectionWorker.exe`，Exe |
| `RelayLogHost.csproj` | `RelayLogHost`、`MemoryLog` | `RelayLogHost.exe`，WinExe |

程序集引用和源清单只在项目文件中维护。共享源码分别编入需要它的组件，保持桌面、连接/保护 worker 和日志 host 的现有进程边界，不增加运行时共享 DLL。

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

## 显示与运行配置

GUI 与 worker 使用 `app.manifest`，保留 `dpiAware=true` 的 SystemAware 声明；`.config` 只声明 .NET Framework 4.8。各运行配置须与其 EXE 相邻。

桌面图标优先从 `../support/wireless.ico` 读取；工作区图标位于 `bin/wireless.ico` 时作为次选。两处均无图标时不影响编译。跨倍率显示器仍由 Windows 整窗缩放，可能插值；本轮没有迁移 UI 框架或更改为 PerMonitorV2。

## 采样与保护调度

1.5.1 的 `PhoneSamplingPolicy` 默认每 3 秒采样，缓存速度只在采样年龄不超过 4 秒时有效。主窗口显示且未最小化时采样，切到 TCP 连接页仍会采样。主窗口最小化或收起到托盘后不再发起新的非安全手机统计查询，已在途的查询允许完成；恢复显示时立即采样并重建速度基线，到下一个完整采样间隔后才显示速度。正常节流不记作采样失败，也不重复添加曲线点。速度为最近采样间隔的平均值，累计流量仍来自手机 VPN 接口计数，不能将暂停采样理解为暂停传输。

独立 worker 的约 5 秒保护检查、手机会话心跳及默认 30 秒失联阈值保持不变。手机守护用 shell 内建 `read`／`printf` 替代小文件读取中的外部 `cat`，活性标记 `watch_tick` 约每 5 秒原子写入一次，写入失败下轮重试。手机每秒检查会话／心跳期限、原有 10 秒守护活性阈值保持不变；这些保护仍按会话运行，不依赖界面统计定时器。这一轮验证外部命令和写入次数以及保护行为，不将其解释为已经测得的耗电降幅。

## 打包边界

打包使用显式文件清单，仅发布未绑定的默认配置，保留第三方 NOTICE/LICENSE 原文。README、LICENSE、源码 BUILD.md 和自写 THIRD-PARTY.md 随运行包提供。

电脑端运行不依赖 PowerShell；工作区 `build.ps1` 只是调用 solution 并生成开发图标的辅助工具，不是运行依赖，不放进安装包。手机守护文本仍内置于 C#，按会话临时传输。

本机 `wireless-config.json`、活动 `state/`、调试输出、真实网络报告、ADB 凭据及备份不属于源码或发布输入。网络检查和进程清理的保护边界不因工程文件拆分而改变。
