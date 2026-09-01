# libsimple — SQLite FTS5 中文/拼音分词器源码

本目录集成了 [wangfenjin/simple](https://github.com/wangfenjin/simple)（v0.5.0，MIT License）
的完整源码，用于生成 StrmAssistant 插件内置的 `libsimple.so`
（`Tokenizer/linux/` 与 `Tokenizer/linux-arm64/` 嵌入式资源）。

`libsimple` 是 SQLite FTS5 的运行时加载扩展，支持**中文分词、拼音搜索、拼音首字母缩写**，
并提供 `simple_query()` / `simple_highlight()` / `simple_snippet()` 等 SQL 函数。

## 目录结构

```
libsimple-src/
├── CMakeLists.txt          # 顶层 CMake（编译 simple 共享库 + sqlite3）
├── LICENSE                 # MIT License（上游 wangfenjin/simple）
├── src/                    # 核心 C++ 源码（分词器/拼音/高亮/入口）
├── contrib/
│   ├── pinyin.txt          # 拼音数据（编译时作为资源嵌入，cmrc）
│   ├── CMakeRC.cmake       # cmrc 资源编译工具
│   └── sqlite3/            # SQLite 3.32.3 源码（编译需要）
└── cppjieba/               # cppjieba 词典（include + deps + dict，纯头文件库）
```

> cppjieba 原为编译时从 GitHub 下载（ExternalProject），本集成已改为**本地引入**
> （见 `src/CMakeLists.txt`），保证离线可重复编译。

## 编译方法

使用 `build-libsimple.sh` 一键编译。**必须在 Ubuntu 18.04（glibc 2.27）环境下编译**，
因为 Emby 容器运行在 glibc 2.27（Debian buster），宿主编译器（如 Debian 13, glibc 2.41）
编出的 .so 在 Emby 里会报 `GLIBC_2.38 not found`。

```bash
# 编译 arm64 和 amd64 两个版本（需要 docker；amd64 需 qemu-user-static）
bash StrmAssistant/Tokenizer/build-libsimple.sh

# 只编译 arm64
bash StrmAssistant/Tokenizer/build-libsimple.sh arm64

# 只编译 amd64
bash StrmAssistant/Tokenizer/build-libsimple.sh amd64
```

脚本会自动：
1. 用 `ubuntu:18.04` 容器（`--platform linux/arm64` / `linux/amd64`）
2. 安装 cmake 3.31（18.04 自带 3.10 太旧，从 Kitware 下载）+ g++
3. `cmake .. -DSIMPLE_WITH_JIEBA=ON` 后 `make`
4. 输出 `libsimple.arm64.so` / `libsimple.amd64.so` 到 `Tokenizer/` 目录

## 更新插件内置的 libsimple.so

编译出新的 .so 后，覆盖对应架构资源并提交：

```bash
cp libsimple.arm64.so Tokenizer/linux-arm64/libsimple.so
cp libsimple.amd64.so Tokenizer/linux/libsimple.so
# 然后 push 触发 GitHub Actions 重新打包 DLL（build-N）
```

> 插件运行时通过 `GetExpectedSha1()` 校验 SHA-1，覆盖新版本后需同步更新
> `StrmAssistant/Mod/EnhanceChineseSearch.cs` 中 `GetExpectedSha1()` 的 SHA-1 值。

## 当前已集成版本

- 上游：wangfenjin/simple @ v0.5.0
- cppjieba：yanyiwu/cppjieba @ 194c144d8b5ed1baf3190d07c5226e804454ab47
- SQLite：3.32.3
- 已构建嵌入资源：
  - `Tokenizer/linux/libsimple.so` — x86-64（SHA1 8e36162f...）
  - `Tokenizer/linux-arm64/libsimple.so` — aarch64（SHA1 8870fff9...）
  - `Tokenizer/win/libsimple.so` — x86-64 Windows（SHA1 aed57350...）

## 参考

- 上游项目：https://github.com/wangfenjin/simple
- cppjieba：https://github.com/yanyiwu/cppjieba
- 拼音搜索用法：`match simple_query('zhoujiel')`（拼音前缀）、`simple_query('zjl')`（首字母缩写）
