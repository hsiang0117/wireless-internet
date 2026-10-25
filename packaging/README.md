# 发布工程

这里只包含开发/打包工具，不是运行时启动入口，不随安装包发布。不自动下载工具，不修改系统 PATH、网卡、路由或手机设置。

`release-files.json` 是显式运行包白名单：源文件、第三方依赖和固定运行文件分开枚举。实际数量由清单计算，不在检查脚本中重复硬编码。`App.config`、三个 csproj、solution、README、LICENSE、BUILD.md 和自写 THIRD-PARTY.md 均包含在新包中；第三方 NOTICE/LICENSE 保持原文。

1.6.0 将连接生命周期分为明确阶段，并增加会话存储、进程管理、手机命令和纯协议解析组件；这些源文件仍分别编入原有 EXE，不增加运行时共享 DLL。GUI 只共用纯 `AndroidProtocol`，worker 编入其余生命周期服务。发布前应核对项目的 Compile 清单与 `SourceFiles` 同步，不能只复制新的 worker 二进制而漏掉可重编所需的源码。

1.8.0 增加 GUI／worker 共用的 `ActionProtocol.cs` 与桌面侧 `WorkerActionClient.cs`，分别编入现有组件；最终结果通过独立本机命名管道传递。此次更新需正常停止共享后一起替换桌面和 worker，不能只在活动会话期间替换观察窗口。

本轮打包保留原有操作锁、默认 30 秒失联保护和 1.5.1 的省电采样行为；版本升级沿用既有绑定格式和会话协议。发布输入必须是冻结源码、已核验依赖和当前文档，不能从活动安装目录整体复制。

## 构建与 staging

`Build-Release.ps1` 的三个输入目录互相独立：

- `SourceDirectory`：有 solution 和源文件的目录，默认查找本 checkout 的 `source/` 或工作区 `gui/`。
- `DependencyDirectory`：已核验的 ADB、gnirehtet 与 support 图标来源目录；不得使用含本机绑定/会话的安装目录。只复制清单声明的依赖文件。
- `DocumentationDirectory`：包含当前版本 `README.md`、`LICENSE`、`notices/THIRD-PARTY.md` 的目录。

例如在独立 checkout 的根目录中：

```powershell
.\packaging\Build-Release.ps1 -SourceDirectory .\source `
    -DependencyDirectory C:\BuildInputs\WirelessInternetDeps `
    -DocumentationDirectory .
```

默认调用现有 MSBuild 的 Release `Rebuild` 并准备 `stage/`，确保新增图标等编译资源后不会沿用较早的增量构建结果；不会启动生成的应用或安装器。stage 只能位于此 checkout 的 `stage/` 或 `artifacts/stage/`，拒绝已有未知文件、设备绑定与 reparse point；不递归删除目录。源清单、第三方文件和 fresh config 精确校验后，生成相对路径 SHA-256 manifest。

需要额外检查本机私有文本时，调用者可传入 `-DisallowedText`；真实值应留在本机调用参数或环境中，不能写入此公共脚本。

编译安装包时额外传入 `-CompileInstaller -InnoCompilerPath <现有ISCC.exe路径>`；可选 `-ChineseMessagesFile <ChineseSimplified.isl路径>`。没有简体中文编译消息文件时安装器使用英文向导；应用自身中文不受影响。输出到 `artifacts/release/`，可通过 `OutputDirectory` 改为其他构建输出目录。

## 验证范围

日常代码修改先运行 `tests/run-tests.ps1` 的 focused 检查；历史离线回归仅在相关模块或发布风险需要时显式运行 `tests/run-full.ps1`（或 `-Full`）。GUI、手机守护和下面的隔离安装检查均为按需入口，不串入默认快速测试。测试布局、保留的安全回归及 `-KeepArtifacts` 见 [tests/README.md](../tests/README.md)。

发布构建仍核对显式文件清单和 payload 哈希；不因测试目录精简而省略编译或发布完整性检查。安装与 DPI 验证按实际改动选择，避免同一源码反复执行无关长测试。
## 隔离安装检查

`Test-Installer.ps1 -SetupPath <已编译安装器路径>` 会在此 checkout 的唯一测试目录完成安装、升级和卸载，并临时写入当前用户卸载项与独立开始菜单分组。**它不是只读测试。** 运行前须正常结束已有共享并退出桌面应用；脚本检测到既有安装注册或应用 presence marker 会拒绝继续，不自动停止任何应用。

检查覆盖 payload 哈希、未绑定配置、旧脚本/旧自写 TXT 精确迁移、用户文件和配置保留，以及会话/应用锁阻断。仅执行随包 ADB 的 `version` 来检查 DLL 装载，不连接手机、不启动或关闭 ADB server，也不启动共享。测试输出、stage、obj/bin、安装目录与结果 JSON 不加入版本控制。
