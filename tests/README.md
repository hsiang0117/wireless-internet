# 离线回归测试

在仓库根目录打开 PowerShell，运行：

```powershell
.\tests\run-tests.ps1
```

脚本默认读取同级 `source/` 中的产品源码，使用 Windows 自带的 .NET Framework x64 C# 编译器，按 C# 5 编译并依次运行下面十一组测试。需要 Windows x64 和 .NET Framework 4.8；不下载工具，也不修改执行策略或系统配置。任何编译失败、测试非零退出码或断言数量不符都会抛出错误并停止。

| 测试组 | 断言数 | 覆盖范围 |
| --- | ---: | --- |
| Network | 69 | 无关 TUN 空字段隔离、物理字段缺失、路由优先级、接口/GUID/网关与端点锁定 |
| Watchdog | 181 | 19 个失联保护场景，覆盖心跳续期、过期、会话替换、进程身份及清理边界 |
| Process | 54 | 参数引用、UTF-8、stdin、子进程环境、双流洪泛、输出限额、超时所有权、继承管道及有界进度回调 |
| Backend | 29 | 连接、保护和统计状态独立映射，采样失败、恢复及会话变化 |
| Sampling | 26 | 前台三秒节流、后台暂停、恢复基线、缓存过期、失败重试及设备变化 |
| MonitoringComposition | 272 | 假观察脚本的快照与调用轨迹，覆盖系统观察、流量统计和快照组装提取边界 |
| Coordinator | 39 | 操作互斥、等待采样、退出排序、异常恢复及并发准入 |
| RuntimeServices | 51 | 原子会话存储、未知字段、虚派发、精确进程归属和输出句柄隔离 |
| Android | 140 | 端点、mDNS、VPN事实解析和手机验证，保留各调用方的重试策略 |
| Lifecycle | 264 | 33 个开始/断开成功及中途失败场景，验证原有命令顺序、锁与清理边界 |
| ConnectionResults | 174 | 21 个结构化阶段结果场景，包含只读历史、失败定位及锁释放异常 |

合计 **1299 项断言**。默认测试不创建或显示 WinForms 窗口。

这些测试不读取真实 WMI 网卡、不调用 ADB、不连接手机。连接流程通过假的上下文执行，不启动真实共享；网络和保护测试使用合成数据，Backend 使用假的观察源。生命周期、服务和保护测试会启动、验证并清理测试自己的假客户端/假中继，不操作正在使用的共享、ADB 服务或其它应用。

每次运行创建独立的 `tests/bin/offline-<随机值>/`，保存编译产物、夹具 PID 文件和协调器结果。没有递归删除操作；该目录可在测试结束后按需清理，且应排除在版本控制和运行安装包之外。输入夹具只使用模拟数据。

如源码目录采用其它布局，可显式指定：

```powershell
.\tests\run-tests.ps1 -SourceDirectory .\path\to\source
```

`-SourceDirectory` 只改变编译输入，生成文件仍留在测试目录自己的 `bin/`。`ProcessExecutionFixture.cs` 只能作为测试子程序使用；`CoordinatorChecks.cs` 保留类名 `UiCoordinatorChecks`，测试的是同一个纯协调器。

本目录是开发回归工具，不是应用运行依赖，也不应安装到运行包中。真实 GUI 视觉、DPI、手机兼容性以及实际网络连通性需要单独验收，本离线套件不覆盖这些项目。

另提供需要现有 Git Bash 的[手机守护隔离测试](PhoneGuardShellChecks.md)，覆盖实际内嵌 shell 逻辑的三十秒边界、会话隔离及读写调用量。它不使用 ADB，模拟锁不替代 Android 实机验证。

## 可选日志视图测试

`ConsoleLogViewChecks.cs` 会显示测试自己的 WinForms 窗口，检查独立日志组件的批次与限额、去重、Unicode、滚动与选择区、后台入队、清空及释放。它不创建应用主窗口，不调用 Backend、ADB 或共享操作，因此单独运行，不加入默认无窗口套件：

```powershell
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$run = Join-Path .\tests\bin ('console-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
& $compiler /nologo /langversion:5 /platform:x64 /target:exe "/out:$run\ConsoleLogViewChecks.exe" /win32manifest:source\app.manifest /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll source\ConsoleLogView.cs tests\ConsoleLogViewChecks.cs
if ($LASTEXITCODE -ne 0) { throw 'ConsoleLogViewChecks 编译失败。' }
Copy-Item -LiteralPath .\source\App.config -Destination "$run\ConsoleLogViewChecks.exe.config"
& "$run\ConsoleLogViewChecks.exe" $run
if ($LASTEXITCODE -ne 0) { throw 'ConsoleLogViewChecks 未通过。' }
```

日志和控件的像素/DPI 对照、完整界面接线和实际手机网络仍单独验收；组件测试通过不等于这些项目已经验证。
