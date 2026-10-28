# 开发检查

日常修改运行快速入口：

~~~powershell
.\tests\run-tests.ps1
~~~

默认运行四组既有快速检查（各 15 项）：操作结果通道、共享配置／会话模型、离线残留恢复、启动清理隔离；另运行 11 个 PC-only 场景（109 条断言）。只使用现有 Windows x64/.NET Framework 4.8 的 C# 5 编译器，不下载工具、不调用真实 ADB、不连接手机，不修改网络设置。PC-only 和操作通道夹具会启动并清理自己的假子进程，其他组使用合成 JSON、内存状态及只读身份检查。

~~~powershell
# 本次取消手机 shell 守护的定向检查
.\tests\run-tests.ps1 -PcOnly
# 按改动选择单组
.\tests\run-tests.ps1 -ModelsOnly
.\tests\run-tests.ps1 -StaleOnly
.\tests\run-tests.ps1 -StartupOnly
~~~

这些筛选开关与 -Full 互斥。-PcOnly 包含 5 个电脑监控场景及 6 个生命周期场景，并仅编译校验受影响的结构化结果夹具。覆盖完整链路才能续期、晚到检查不复活、默认 30 秒电脑超时、ADB 离线不宣称手机已停、开始时不下发或启动手机脚本、正常断开顺序、提交前后回滚和旧守护会话的升级边界。超时使用模拟单调时钟，不真实等待 30 秒。

模型检查覆盖 PcOnly + 空手机脚本路径、旧会话读取兼容、未知模式拒绝、字段精度、不同读取方的缺省和校验规则、未知嵌套字段保留。旧手机守护记录仅用于本地失效恢复和升级拒绝场景；新版不会接管仍活动的旧守护会话。

-StaleOnly 检查三类记录进程、未知归属、端口查询失败、PID 复用、token 替换和删除确认。-StartupOnly 另外确认忙锁立即跳过、正常共享保留和独立启动操作；手机控制 API 均为禁止调用的夹具陷阱。实际主实例启动接线由 GUI 启动验收。

源码默认取 source/（兼容开发副本 gui/），也可显式指定：

~~~powershell
.\tests\run-tests.ps1 -SourceDirectory .\path\to\source
~~~

## 目录与按需回归

| 路径 | 用途 |
| --- | --- |
| focused/ | 操作通道、共享模型、失效恢复与启动隔离 |
| regression/ | PC 监控、网络、进程、UI 监控及生命周期回归 |
| fixtures/ | 进程执行的共用测试子程序 |
| optional/ | 会显示窗口的日志控件检查 |
| TestSupport.ps1 | 共用编译、结果验证及输出目录归属清理 |

历史全量通过下面入口显式选择，不作为日常默认动作：

~~~powershell
.\tests\run-full.ps1
# 或
.\tests\run-tests.ps1 -Full
~~~

全量保留 Network、Watchdog、Process、Monitoring、Coordinator、RuntimeServices、Android、Lifecycle、ConnectionResults 九组，按实际通过的断言数报告。取消手机守护后已删除其脚本导出器、Bash 模拟和专属心跳协议用例；生命周期及 PC 监控回归保留锁、归属、提交边界、终态超时和精确清理。ConnectionResultChecks.cs 复用 ConnectionLifecycleChecks.cs 的隔离夹具，需一起编译。

## 可选检查与生成文件

~~~powershell
# 显示测试自己的日志控件窗口
.\tests\optional\run-console-checks.ps1
~~~

GUI、DPI、真实手机和网络连通性按相关改动单独验收。测试只在 tests/bin/q-<随机值>/（快速）、f-...（全量）或 c-...（窗口）生成本次文件。成功后默认删除自己的有归属标记目录；失败或 -KeepArtifacts 时保留。清理核对绝对路径、标记、重解析点及仍运行的自有测试进程，不清理历史目录或产品状态。

tests/ 仅用于开发，不是运行依赖，不随安装包发布。编译失败、非零退出或结果不符都会停止入口。
