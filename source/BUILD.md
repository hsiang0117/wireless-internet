# 源码与构建

本文对应无线网络共享 1.4.2。使用流程见仓库根目录的 [README](../README.md)，离线测试见 [tests/README.md](../tests/README.md)。

## 构建范围

电脑端使用 C# 5、.NET Framework 4.8 和 Windows x64。当前仓库提供独立的 `.cs` 源文件，没有随仓库提供 `App.config`、`build.ps1` 或安装包打包工程；下面直接调用 .NET Framework 的 `csc.exe` 构建三个可执行组件，不生成安装包。

| 输出 | 源文件 | 显式程序集引用 | 目标类型 |
| --- | --- | --- | --- |
| `WirelessInternet.exe` | `Program.cs`、`Backend.cs`、`MemoryLog.cs`、`AppForm.cs`、`SetupForm.cs`、`SetupBackend.cs`、`UiDpi.cs` | `System.Windows.Forms.dll`、`System.Drawing.dll`、`System.Web.Extensions.dll` | `winexe` |
| `support/ConnectionWorker.exe` | `WorkerHost.cs`、`RuntimeCommon.cs`、`NativeNetwork.cs`、`ConnectionController.cs`、`WatchdogController.cs`、`PhoneGuardSource.cs` | `System.Web.Extensions.dll`、`System.Management.dll` | `exe` |
| `support/RelayLogHost.exe` | `RelayLogHost.cs`、`MemoryLog.cs` | `System.Web.Extensions.dll` | `winexe` |

三个组件分别编译，不能将 `Program.cs` 与 `WorkerHost.cs` 混在同一次编译中。`ConnectionWorker.exe` 保留控制台型 `exe` 目标以提供操作结果；桌面程序启动它时重定向输出并隐藏控制台窗口。

## 配置与显示缩放

- 桌面程序的实际运行配置是根目录的 [WirelessInternet.exe.config](../WirelessInternet.exe.config)。后台组件对应的配置在 [support/ConnectionWorker.exe.config](../support/ConnectionWorker.exe.config) 和 [support/RelayLogHost.exe.config](../support/RelayLogHost.exe.config)。这些文件声明 .NET Framework 4.8，须与对应可执行文件相邻。
- 系统 DPI 感知由 [app.manifest](app.manifest) 中的 `dpiAware=true` 声明。界面以 96 DPI 为设计基准，按 Windows 系统 DPI 缩放，可用空间较小时使用滚动区域。跨不同缩放比例的显示器时，Windows 会统一缩放窗口，可能出现插值模糊；运行中更改系统缩放后应退出并重新打开程序。
- 手机绑定模板是 [wireless-config.example.json](../wireless-config.example.json)。每台电脑实际使用自己的 `wireless-config.json`；运行状态位于 `state/`。不要将本机绑定、配对凭据或活动会话状态加入源码提交。

## 编译命令

在仓库根目录打开 PowerShell。以下命令在系统临时目录下新建唯一输出目录，只编译和复制配置，不运行程序，不覆盖仓库或正在运行的可执行文件。需要本机已启用 .NET Framework 4.8，并存在对应的 64 位 Framework 编译器。

```powershell
$repo = (Get-Location).Path
$src = Join-Path $repo 'source'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '未找到 .NET Framework C# 编译器。' }
$out = Join-Path ([IO.Path]::GetTempPath()) ('WirelessInternet-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $out 'support') -Force | Out-Null
$common = @('/nologo', '/langversion:5', '/optimize+', '/utf8output', '/platform:x64')

$desktopSources = @('Program.cs', 'Backend.cs', 'MemoryLog.cs', 'AppForm.cs',
    'SetupForm.cs', 'SetupBackend.cs', 'UiDpi.cs') |
    ForEach-Object { Join-Path $src $_ }
& $csc @common '/target:winexe' "/win32manifest:$src\app.manifest" "/out:$out\WirelessInternet.exe" `
    '/r:System.Windows.Forms.dll' '/r:System.Drawing.dll' '/r:System.Web.Extensions.dll' @desktopSources
if ($LASTEXITCODE -ne 0) { throw '桌面程序编译失败。' }

$workerSources = @('WorkerHost.cs', 'RuntimeCommon.cs', 'NativeNetwork.cs',
    'ConnectionController.cs', 'WatchdogController.cs', 'PhoneGuardSource.cs') |
    ForEach-Object { Join-Path $src $_ }
& $csc @common '/target:exe' "/win32manifest:$src\app.manifest" "/out:$out\support\ConnectionWorker.exe" `
    '/r:System.Web.Extensions.dll' '/r:System.Management.dll' @workerSources
if ($LASTEXITCODE -ne 0) { throw '连接后台组件编译失败。' }

$relaySources = @('RelayLogHost.cs', 'MemoryLog.cs') | ForEach-Object { Join-Path $src $_ }
& $csc @common '/target:winexe' "/out:$out\support\RelayLogHost.exe" `
    '/r:System.Web.Extensions.dll' @relaySources
if ($LASTEXITCODE -ne 0) { throw '中继日志组件编译失败。' }

foreach ($relative in @('WirelessInternet.exe.config',
    'support\ConnectionWorker.exe.config', 'support\RelayLogHost.exe.config')) {
    Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination (Join-Path $out $relative)
}
Write-Output $out
```

编译结果只包含三个程序及运行配置，并非完整共享工具目录；实际运行还需要对应目录布局中的 ADB、gnirehtet relay/APK、图标与许可声明。准备独立运行目录时应保留第三方文件及其原声明，并从未绑定模板建立自己的配置。不要把编译结果直接覆盖到仍有共享会话或后台进程的目录。

上述命令省略自定义图标的编译选项，不影响连接逻辑。C# 编译选项见 [Microsoft 文档](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/)，运行配置的 `supportedRuntime` 含义见 [Microsoft 文档](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/startup/supportedruntime-element)。

## 运行时边界

电脑端连接、断开、网络读取和保护监控均由 C# 组件执行，不依赖 PowerShell。`PhoneGuardSource.cs` 包含手机端会话守护内容；程序在需要时生成临时传输文件并交给 ADB，下发后删除本机临时文件。

1.4.2 修复了无关 VPN/TUN 虚拟接口的空路由指标影响启动或心跳检查的问题，仍严格校验选定物理 Wi-Fi 和有线网卡的必要指标及接口身份，不修改 TUN/VPN 路由规则。此项修复不构成对所有 VPN、驱动或手机组合的兼容承诺。
