# 手机守护的隔离 shell 回归

本测试可选，需要已经安装的 Git for Windows 和 .NET Framework x64 编译器，不安装依赖。它单独测试编入 C# 的手机 shell 守护；默认读取 `source/PhoneGuardSource.cs`：

```powershell
.\tests\run-phone-guard-checks.ps1
```

可指定工作副本或旧版本源码，对照相同的模拟 60 秒健康会话：

```powershell
.\tests\run-phone-guard-checks.ps1 -SourceFile .\path\to\PhoneGuardSource.cs -BaselineSourceFile .\path\to\previous\PhoneGuardSource.cs
```

脚本自动从现有 Git 和 Program Files 查找 Bash，也可通过 `-BashPath` 指定已有 `bash.exe`。编译产物和假状态文件仅位于测试自己的 `bin/phone-guard-<随机值>/`；不要把这些生成文件装入运行包或提交到版本控制。

测试先按 C# 5/x64 编译常量导出器，再对导出的原始守护及夹具执行 Bash 语法检查。实际执行的是隔离副本：根目录、单调时钟读取和 Android 命令均替换为夹具；`flock` 是串行逻辑 stub，不是真实内核锁。

覆盖 30 秒边界和 29 秒不提前停止、每 5 秒心跳持续有效、等待锁时收到新心跳、旧 token 不能停止新会话、取消、过期后拒绝续期、守护死亡/活性过旧、时钟回退、VPN 限制与停止失败的终态，以及 `read_file` 的空文件、无尾换行、多行、空白和反斜杠语义。测试不运行 ADB、不连接手机、不修改系统设置或网络。

当前新旧对照为 **107 项通过**。相同模拟 60 秒、12 次成功心跳中：

| 计数 | 旧版 | 新版 |
| --- | ---: | ---: |
| `read_file` 的外部 `cat` 执行 | 253 | 0 |
| 活性标记 `watch_tick` 写入 | 61 | 12 |
| `mv` 总执行 | 74 | 25 |
| 一秒检查循环 | 60 | 60 |

该计数反映命令和写入减少，不代表实际耗电降幅。`$()` 命令替换仍可能派生 shell 子进程；文件读取和一秒检查仍保留。Android shell、内核锁、后台调度、真实 VPN 停止，以及断联后的实际 30 秒保护必须在手机上另行验收。
