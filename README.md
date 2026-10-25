# 无线网络共享

Windows 桌面工具，通过安卓原生无线调试和 gnirehtet 提供网络共享，包含开始连接、断开连接、手机绑定、系统托盘、实时连接日志、流量与网速统计，以及约 30 秒的失联保护。

当前版本：**1.4.2**。

## 使用

运行 `WirelessInternet.exe`，按绑定向导完成无线调试配对。需要 Windows 10/11 x64、.NET Framework 4.8 和 Android 11+；手机须支持 Wi-Fi、热点与原生无线调试同时开启。首次启动共享时，在手机上允许安装及 VPN 请求。

完整步骤见 [使用说明](使用说明.txt) 和 [桌面版使用说明](桌面版使用说明.txt)。每台电脑保存自己的手机绑定；`wireless-config.example.json` 是未绑定模板，真实的 `wireless-config.json` 与运行会话不提交到仓库。

## 1.4.2 修复

部分 VPN/TUN 虚拟网卡的 `InterfaceMetric` 字段为空，会使此前版本在开始连接或后台心跳检查时读表失败。现在先过滤无关虚拟接口，再解析活动物理 Wi-Fi／有线候选网卡的数据；物理网卡缺失必要指标时仍明确报错，保留原有路由优先级、网关与接口身份校验。

已在当前 Clash Verge／Mihomo TUN 开启环境中验证连接、断开、重连和手机 HTTPS 上网；请求通过正常证书校验，并由对应中继连接记录及 VPN 流量计数确认走共享通道。保护心跳也持续超过原 30 秒失联期限。结果仅覆盖所测环境，其他 VPN、驱动和手机组合仍需验证。

## 开发与测试

电脑端控制逻辑为 C# 5／.NET Framework 4.8，源码在 `source/`。编译组件和引用说明见 [source/BUILD.txt](source/BUILD.txt)，离线回归运行方法见 [tests/README.md](tests/README.md)。

ADB、gnirehtet 及手机端组件的来源与许可见 [第三方组件说明](notices/第三方组件说明.txt)。
