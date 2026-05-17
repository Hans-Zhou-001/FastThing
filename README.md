# FastThing ⚡

<p align="center">
  <img src="https://github.com/Hans-Zhou-001/FastThing/app.ico" width="64" alt="FastThing Icon"/>
</p>

<p align="center">
  <strong>极速文件搜索工具</strong> · 基于 NTFS MFT，毫秒级索引，Everything 风格界面<br>
  <em>Lightning-fast file search utility based on NTFS MFT, millisecond-level indexing</em>
</p>

<p align="center">
  <a href="https://github.com/Hans-Zhou-001/FastThing/releases">
    <img src="https://img.shields.io/github/v/release/Hans-Zhou-001/FastThing?color=blue" alt="Release"/>
  </a>
  <a href="https://github.com/Hans-Zhou-001/FastThing/blob/main/LICENSE">
    <img src="https://img.shields.io/github/license/Hans-Zhou-001/FastThing" alt="License"/>
  </a>
  <a href="https://dotnet.microsoft.com/">
    <img src="https://img.shields.io/badge/.NET-8.0-512bd4" alt=".NET 8"/>
  </a>
  <img src="https://img.shields.io/badge/platform-Windows%20x64-lightgrey" alt="Windows x64"/>
</p>

---

## ✨ 特性 / Features

- ⚡ **毫秒级搜索** — 基于 NTFS MFT 直接读取，无需遍历目录
- ⚡ **Millisecond Search** — Reads NTFS MFT directly, no directory traversal needed
- 🚀 **即时启动** — 首次索引后直接加载缓存，开机即搜
- 🚀 **Instant Startup** — Loads cached index on launch, search immediately
- 🎯 **Everything 风格界面** — 熟悉的交互体验，零学习成本
- 🎯 **Everything-style UI** — Familiar UX, zero learning curve
- 🔍 **实时搜索** — 输入即搜索，支持文件名快速匹配
- 🔍 **Real-time Search** — Search-as-you-type with filename matching
- 📋 **右键菜单** — 打开文件、打开所在文件夹、复制路径、复制文件名
- 📋 **Context Menu** — Open, Open containing folder, Copy path, Copy name
- 📊 **虚拟列表** — 百万文件列表不卡顿
- 📊 **Virtual List** — Handles millions of files without lag
- 🔧 **后台索引更新** — 启动时后台自动更新索引，不影响使用
- 🔧 **Background Indexing** — Index rebuilds in background on startup

---



## 📦 下载 / Downloads

前往 [Releases](https://github.com/Hans-Zhou-001/FastThing/releases) 页面下载 / Get the latest release at [Releases](https://github.com/Hans-Zhou-001/FastThing/releases):

| 版本 / Version | 说明 / Description |
|---|---|
| `FastThing.exe`（框架依赖 / Framework-dependent） | 需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)，文件体积小（~300 KB）/ Requires .NET 8 Desktop Runtime, small (~300 KB) |
| `FastThing.exe`（独立发布 / Self-contained） | 无需安装运行时，单文件 ~65 MB / No Runtime needed, single file ~65 MB |

---

## 🛠️ 构建 / Build

### 环境要求 / Requirements

- Windows 10 / 11
- [Visual Studio 2022](https://visualstudio.microsoft.com/zh-hans/vs/)（需安装 .NET 桌面开发工作负载 / Requires .NET desktop development workload）
- 或 / Or: .NET 8 SDK + VS 2022 Developer Command Prompt

### 克隆仓库 / Clone the Repo

```bash
git clone https://github.com/Hans-Zhou-001/FastThing.git
cd FastThing
