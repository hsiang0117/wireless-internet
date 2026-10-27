# 开发检查

日常修改运行快速入口：

```powershell
.\tests\run-tests.ps1
```

默认编译并运行四组快速检查：15 项 Worker→GUI 操作协议、15 项共享配置／会话兼容性、15 项离线残留会话恢复，以及 15 项启动清理隔离检查。目标为数秒至十余秒，不自动运行历史全量、GUI、手机守护或安装测试。只使用现有 Windows x64/.NET Framework 4.8 的 C# 5 编译器，不下载工具、不调用真实 ADB、不连接手机，也不修改网卡、路由或系统配置。

仅验证启动自动清理时使用 `./tests/run-tests.ps1 -StartupOnly`。它确认忙锁立即跳过、正常共享及心跳保持、模糊资源不清理、二次 token／资源核验、删除确认和锁释放；所有手机、在线 Stop、进程和网络控制 API 均为禁止调用的夹具陷阱。正常主实例的启动接线另由实际 GUI 启动验收，预览、采样探测、截图和二次启动按 Program 门禁保持只读。

仅验证离线断开恢复时使用 `./tests/run-tests.ps1 -StaleOnly`。它检查三类记录进程、未知归属和端口查询失败、PID 复用、token 替换、删除确认及操作锁释放；使用内存会话和模拟端口，只对当前测试进程及一个不存在的 PID 做只读身份检查，不创建或结束进程。

仅修改共享模型时，可单独运行本轮受影响路径，跳过操作协议的假 worker：

```powershell
.\tests\run-tests.ps1 -ModelsOnly
```

模型检查仅在本次生成目录读写合成 JSON，覆盖字段与数值精度、GUI／Worker 的不同缺省和校验规则、GUI 对非观察字段的容错、未知嵌套字段写回，以及配置只读不补字段和整数转换边界。

源码默认从仓库的 `source/` 读取，也支持开发工作副本的 `gui/`。需要时显式指定：

```powershell
.\tests\run-tests.ps1 -SourceDirectory .\path\to\source
```

## 目录与按需回归

| 路径 | 用途 |
| --- | --- |
| `focused/` | 默认运行的操作协议与共享配置／会话兼容性检查 |
| `regression/` | 网络、保护、进程、监控、生命周期等历史离线回归 |
| `fixtures/` | 进程执行与手机守护导出器的共用测试子程序 |
| `optional/` | 会显示窗口的日志组件检查，以及需要现有 Git Bash 的手机守护检查 |
| `TestSupport.ps1` | 共用编译、结果验证和本次生成目录的归属清理 |

历史全量需要主动选择：

```powershell
.\tests\run-full.ps1
# 等价入口
.\tests\run-tests.ps1 -Full
```

全量保留九组、1034 项明确检查，不作为每次修改的默认动作：

| 测试组 | 检查数 | 主要覆盖 |
| --- | ---: | --- |
| Network | 69 | TUN 字段隔离、物理网卡、路由选择、接口与端点锁定 |
| Watchdog | 181 | 心跳、默认 30 秒失联保护、会话替换、进程身份与清理边界 |
| Process | 54 | 参数、双流输出、超时归属、继承管道与有界进度 |
| Monitoring | 62 | 状态独立映射、三秒采样、后台暂停、恢复与缓存、设备切换、有界日志 |
| Coordinator | 39 | 操作互斥、等待采样、退出排序与异常恢复 |
| RuntimeServices | 51 | 原子会话存储、精确进程归属与输出句柄隔离 |
| Android | 140 | 端点、mDNS、VPN 事实解析及手机验证 |
| Lifecycle | 264 | 开始/断开与中途失败，命令顺序、锁与清理边界 |
| ConnectionResults | 174 | 结构化阶段结果、只读历史、失败定位与锁释放异常 |

原来的 `BackendStatusChecks`、`PhoneSamplingChecks` 和 `MonitoringCompositionChecks` 已合并为 `regression/MonitoringChecks.cs`，共用一个假观察源。保留前两者的 55 项明确语义检查，以及日志上限、无效输入和 preview 观察隔离；移除重复逐帧通用断言和大批快照 JSON 导出。保护、锁、进程归属及失败清理用例保留。

这些历史回归使用合成数据及假观察源；生命周期与进程测试会启动并清理自己的假进程。`ConnectionResultChecks.cs` 会与 `ConnectionLifecycleChecks.cs` 一起编译以复用夹具，不要将后者单独删除。

## 可选检查与生成文件

```powershell
# 显示测试自己的日志控件窗口，不启动应用主窗口
.\tests\optional\run-console-checks.ps1
# 需要现有 Git Bash；模拟 Android 命令和时钟，不使用 ADB
.\tests\optional\run-phone-guard-checks.ps1
```

手机守护的边界和模拟范围见 [optional/PhoneGuardShellChecks.md](optional/PhoneGuardShellChecks.md)。真实 GUI 视觉、DPI、手机兼容性、Android 内核锁及网络连通性仍需按相关改动另行验收，不在默认快速检查中重复运行。

每次仅在 `tests/bin/q-<8位随机值>/`（快速）、`f-...`（全量）、`c-...`（窗口）或 `g-...`（守护）生成文件，使用短路径减少 Windows 路径长度问题。成功后默认删除本次有归属标记的目录；失败或指定 `-KeepArtifacts` 时保留。清理前核对绝对路径、标记、重解析点及该目录内仍运行的测试进程，不清理旧目录、产品 `bin/` 或其他会话。

```powershell
.\tests\run-tests.ps1 -KeepArtifacts
```

`tests/` 仅用于开发，不是应用运行依赖，不随运行安装包发布。编译失败、测试非零退出或结果不符都会停止当前入口。
