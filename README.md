# Emby神医助手

![logo](StrmAssistant/Properties/thumb.png "logo")

## 基于社区版，使用AI进行对最新版Emby进行适配，有问题不解决，请转至[Pro版本](https://github.com/sjtuross/StrmAssistant)

## [English](README.en.md)

## 版本信息

- **插件版本**: 26.3.25
- **最低Emby版本**: 4.9.1
- **推荐Emby版本**: 4.9.1.90 或更高
- **测试通过版本**: 4.9.1.90、4.10.0.29
- **目标框架**: .NET 6.0

### 最新优化 (v26.3.25)

✅ **arm64/aarch64 全面支持（2026-09 新增）**
- **Harmony 2.4.2**：升级 Lib.Harmony 2.3.6 → 2.4.2，修复 arm64 下所有补丁抛 `NotImplementedException` 的问题，全部模块在 arm64 上恢复真正的 Harmony 补丁
- **中文搜索增强 arm64**：编译 arm64 版 `libsimple.so`（SQLite FTS5 中文/拼音分词器），arm64 服务器也可开启中文模糊搜索/拼音搜索
- **libsimple 源码集成**：`Tokenizer/libsimple-src/` 收录完整源码 + 一键编译脚本，可离线交叉编译 arm64/amd64

✅ **全面的架构重构**
- 引入新的`EmbyVersionAdapter`，更智能的版本检测和适配
- 创建`ServiceLocator`服务定位器，统一管理全局服务
- 增强的`PatchTracker`，提供详细的补丁状态跟踪

✅ **完全支持 Emby 4.9.1.90 / 4.10.0.29**
- 专门为4.9.1.90和4.10.0.29版本优化的API调用
- 支持最新的MediaSources和通知系统增强
- 支持 Emby 4.9.1 及以上版本

✅ **增强的错误处理**
- 更详细的诊断日志和错误追踪
- 多层回退机制：Harmony → Reflection → PublicAPI
- 智能的功能降级，核心功能保证可用

## 用途

1. 提高首次播放的起播速度
2. 视频截图预览缩略图增强
3. 片头片尾探测增强
4. 自动合并同目录视频为多版本
5. 独占模式提取媒体信息
6. 独立的外挂字幕扫描
7. 自定义刮削备选语言
8. 使用替代`TMDB`配置
9. 演职人员增强`TMDB`
10. 获取原语言海报
11. 中文搜索增强
12. 拼音首字母排序
13. 媒体信息持久化
14. 支持代理服务器
15. 支持`TMDB`剧集组刮削

## 安装与使用说明请查看 [Wiki](https://github.com/sjtuross/StrmAssistant/wiki)

## 兼容性说明

本插件已针对Emby最新版本进行优化，采用动态适配机制：

- **自动版本检测**: 插件启动时自动检测Emby版本
- **智能API适配**: 根据版本自动选择正确的API调用方式
- **Harmony补丁支持**: 如果支持Harmony，优先使用补丁方式
- **反射回退**: Harmony不可用时自动使用反射调用
- **公共API兜底**: 反射失败时回退到公共API，确保功能可用

### 已知兼容版本

| Emby版本 | 兼容状态 | 备注 |
|---------|---------|------|
| 4.10.0.29 | ✅ 完全支持 | 已在 x86_64 服务器实测（全部模块 Harmony + 中文搜索增强） |
| 4.9.1.90+ | ✅ 完全支持 | 当前推荐版本，性能最优 |
| 4.9.1.80-89 | ✅ 完全支持 | 稳定版本 |
| 4.9.1.0-79 | ✅ 支持 | 部分功能可能降级 |
| < 4.9.1.0 | ❌ 不支持 | 已移除兼容逻辑 |

### 架构支持

| 架构 | 兼容状态 | 备注 |
|---------|---------|------|
| x86-64 / x64 | ✅ 完全支持 | 所有功能可用 |
| ARM64 / aarch64 | ✅ 完全支持 | 需 Harmony 2.4.2+，含中文搜索增强 |

## 构建说明

### 方式一：GitHub Actions（推荐）

修改代码后推送到 GitHub，CI 自动编译并发布 `build-N` Release：

```bash
git add -A && git commit -m "..." && git push
```

然后从 [Releases](https://github.com/myp015/StrmAssistant/releases) 下载最新 `build-N` 的 `StrmAssistant.dll`，部署到 `config/plugins/StrmAssistant.dll` 后重启 Emby。

### 方式二：本地构建

- 需要 .NET 6.0 SDK 或更高
- Linux 下请使用 `build-local.sh`（绕开 Resource.Embedder 的 Linux 路径问题）

```bash
bash build-local.sh
```

构建产物位于: `StrmAssistant/bin/Release/StrmAssistant.merged.dll`

> 注意：本地构建时 `Resource.Embedder`（卫星资源合并）在 Linux 上有 bug，脚本已自动绕过。

### 编译 libsimple.so（中文/拼音分词器）

源码与一键脚本位于 `StrmAssistant/Tokenizer/libsimple-src/`，详见该目录 README。


## 声明

本项目为开源项目，与 Emby LLC 没有任何关联，也未获得 Emby LLC 的授权或认可。本项目的目的是为合法购买并安装了 Emby 软件的用户提供额外的功能增强和使用便利。

### 使用须知

1. **合法使用**  
   本项目仅适用于合法安装和使用 Emby 软件的用户。使用本项目时，用户需自行确保遵守 Emby 软件的服务条款和使用许可协议。

2. **非商业用途**  
   本项目完全免费，仅限个人学习、研究和非商业用途。严禁将本项目或其衍生版本用于任何商业用途。

3. **不包含 Emby 专有组件**  
   本项目未包含 Emby 软件的任何专有组件（例如：DLL 文件、代码、图标或其他版权资源）。使用本项目不会直接修改或分发 Emby 软件本身。

4. **功能限制**  
   本项目不会绕过 Emby 的授权机制、数字版权保护 (DRM)，或以任何方式解锁其付费功能。本项目仅在运行时动态注入代码，且不会篡改 Emby 软件的核心功能。

5. **用户责任**  
   用户在使用本项目时，需自行承担遵守相关法律法规的责任。如果用户使用本项目违反了 Emby 的服务条款或相关法律法规，本项目开发者概不负责。

### 免责声明

1. 本项目开发者不对因使用本项目而可能导致的任何直接或间接后果（包括但不限于数据丢失、软件故障或法律纠纷）负责。
2. 如果认为本项目可能侵犯相关方的合法权益，请与开发者取得联系。

### 星星数

[![Star History Chart](https://api.star-history.com/svg?repos=sjtuross/strmassistant&type=Date)](https://www.star-history.com/#sjtuross/strmassistant&Date)
