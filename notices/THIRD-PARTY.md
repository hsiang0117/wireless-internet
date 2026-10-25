# 第三方组件与许可

本项目自有代码采用仓库根目录 [LICENSE](../LICENSE) 中的 MIT 许可。该许可不替代 ADB、Android SDK Platform-Tools、gnirehtet 或其他第三方组件的原有许可与通知；分发这些文件时，仍须保留相应的上游声明。

## Android SDK Platform-Tools 37.0.1

- 来源：[Android 官方 Platform-Tools 发布页](https://developer.android.com/tools/releases/platform-tools)；ADB 用法见 [Android 官方文档](https://developer.android.com/tools/adb)。
- 本仓库版本记录：[platform-tools/source.properties](../platform-tools/source.properties)，当前为 `Pkg.Revision=37.0.1`。
- 随附组件的原许可与版权通知：[platform-tools/NOTICE.txt](../platform-tools/NOTICE.txt)。此文件应原样保留。

仓库的 `platform-tools/` 保留了 Windows Platform-Tools 发布包中的额外工具，包括 `fastboot.exe`、`sqlite3.exe`、`etc1tool.exe` 等。应用的共享流程使用 ADB，无需运行这些额外工具；这里不包含完整 Android SDK、Android Studio 或 Java 开发环境。

1.4.2 安装包中的 `platform-tools/` 为精简载荷，仅包含 ADB、其运行 DLL、`NOTICE.txt` 和 `source.properties`。仓库目录与该安装包的文件范围不同；后续分发范围应以对应版本的实际文件清单为准。

## Genymobile gnirehtet v2.5.1

- 来源：[gnirehtet 项目](https://github.com/Genymobile/gnirehtet) 与 [v2.5.1 发布页](https://github.com/Genymobile/gnirehtet/releases/tag/v2.5.1)。
- 使用组件：Windows Rust relay `gnirehtet.exe` 和 Android 客户端 `gnirehtet.apk`。
- 许可：Apache License 2.0。随附原文位于 [gnirehtet-rust-win64/LICENSE](../gnirehtet-rust-win64/LICENSE)；亦可参阅 [上游对应标签的 LICENSE](https://github.com/Genymobile/gnirehtet/blob/v2.5.1/LICENSE)。本说明不复制或改写其许可原文。

gnirehtet 使用 Android VPN 接口转发 IPv4 TCP/UDP，目前不支持 IPv6；其手机端 VPN 与同一手机上的其他 VPN 存在使用冲突。这与电脑端其他 VPN/TUN 软件的兼容性是不同的问题，不能互相推定。上游 README 说明该项目不再积极维护，仅修复主要阻断问题。

## 发布记录中的下载校验值

以下 SHA-256 沿用本项目原有发行记录，标识当时下载的 Windows ZIP 压缩包；它们不是当前仓库目录、单个二进制或安装包的哈希，本次文档整理没有重新下载这些 ZIP。

| 上游压缩包 | 记录的 SHA-256 |
| --- | --- |
| Android SDK Platform-Tools 37.0.1 Windows ZIP | `45F4D63113E895EBDE0C90F194099A4676B6AC653BD28D54314A9E022BBC1A99` |
| gnirehtet v2.5.1 Windows ZIP | `7F5B1063E7895182AA60DEF1437E50363C3758144088DCD079037BB7C3C46A1C` |

## 安装包构建工具

1.4.2 安装包的构建记录使用 Inno Setup 6.7.3，来源为 [Inno Setup 官方下载页](https://jrsoftware.org/isdl.php)。Inno Setup 编译器是开发端工具，不随本仓库的运行目录或最终安装包载荷安装到用户电脑；本项目的 MIT 许可不替代该工具自身的许可。

日常使用无需安装 C# 编译器或 Inno Setup；构建本项目 C# 组件的方法见 [source/BUILD.md](../source/BUILD.md)。

## 安装器简体中文译文

简体中文安装向导使用 [kira-96/Inno-Setup-Chinese-Simplified-Translation](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation) 的 `ChineseSimplified.isl`（6.5.0+）。仓库保留原译者文件头、译文副本和上游 MIT 许可；该许可独立于本项目自有代码的 MIT 许可。消息嵌入安装器，原许可如下：
MIT License

Copyright (c) 2019 - 2020 kirakira

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
