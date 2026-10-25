# 离线回归测试

这些测试只使用合成网卡数据和测试自己的子进程，不读取真实 WMI 网卡、不调用 ADB，也不连接手机或修改网络设置。

在仓库根目录打开 PowerShell，使用 Windows 自带的 .NET Framework C# 编译器运行：

```powershell
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
New-Item -ItemType Directory -Path tests\bin -Force | Out-Null

& $compiler /nologo /langversion:5 /platform:x64 /target:exe /out:tests\bin\NativeNetworkTunRegression.exe /r:System.Management.dll source\NativeNetwork.cs tests\NativeNetworkTunRegression.cs
& .\tests\bin\NativeNetworkTunRegression.exe

& $compiler /nologo /langversion:5 /platform:x64 /target:exe /out:tests\bin\WatchdogChecks.exe /r:System.Management.dll /r:System.Web.Extensions.dll source\RuntimeCommon.cs source\NativeNetwork.cs source\WatchdogController.cs source\PhoneGuardSource.cs tests\WatchdogChecks.cs
& .\tests\bin\WatchdogChecks.exe
```

每个命令均应返回退出码 0。当前测试结果：

- 网卡读取：69 项检查，覆盖无关 TUN 空字段、物理候选网卡缺失字段、路由优先级、网关和接口身份校验。
- 失联保护：19 个场景、181 项断言，覆盖心跳续期、超时、会话变化、进程身份和清理边界。

失联保护测试会启动并清理自己的假中继子进程；不会操作真实共享进程。结果输出到控制台；编译产物所在的 `tests/bin/` 不纳入版本控制。
