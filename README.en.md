# Emby StrmAssistant

![logo](StrmAssistant/Properties/thumb.png "logo")

## Community Edition - AI-Optimized for Latest Emby

## [中文](README.md)

## Purpose

1. Improve initial playback start speed
2. Enhance image capture and thumbnail preview
3. Intro/credits detection based on playback behavior
4. Independent external subtitle scanning

## Latest Optimizations (v26.3.25)

✅ **Complete Architecture Refactoring**
- New `EmbyVersionAdapter` for intelligent version detection and adaptation
- `ServiceLocator` for unified global service management
- Enhanced `PatchTracker` with detailed patch status tracking

✅ **Full Support for Emby 4.9.1.90**
- Optimized API calls specifically for version 4.9.1.90
- Support for latest MediaSources and notification system enhancements
- Supports Emby 4.9.1 and above only

✅ **Enhanced Error Handling**
- More detailed diagnostic logging and error tracking
- Multi-layer fallback mechanism: Harmony → Reflection → PublicAPI
- Intelligent feature degradation with core functionality guaranteed

### Previous Optimizations (v2.1.0)
1. Support concurrent tasks
2. Support non-strm media imported with ffprobe blocked
3. Include media extras
4. Process media items by release date in the descending order
5. Add plugin config page with library multi-selection
6. Image capture enhanced
7. Introduce catch-up mode
8. Playback behavior-based intro and credits detection for episodes
9. Independent external subtitle scan

## Install

1. Download `StrmAssistant.dll` to the `plugins` folder
2. Restart Emby
3. Go to the Plugins page and check the plugin version and settings

## Version Info

- **Plugin Version**: 26.3.25
- **Minimum Emby Version**: 4.9.1
- **Recommended Emby Version**: 4.9.1.90 or higher
- **Tested On**: 4.9.1.90
- **Target Framework**: .NET 6.0

## Latest Optimizations (v26.3.25)

✅ **Full ARM64/aarch64 support (new in 2026-09)**
- **Harmony 2.4.2**: upgraded Lib.Harmony 2.3.6 → 2.4.2, fixing `NotImplementedException` on every patch on arm64; all modules now use real Harmony patches on ARM64
- **Chinese search enhancement on arm64**: built arm64 `libsimple.so` (SQLite FTS5 Chinese/Pinyin tokenizer), enabling Chinese fuzzy & Pinyin search on ARM64 servers
- **libsimple source integrated**: full source + one-click build script in `Tokenizer/libsimple-src/`

## Architecture Support

| Architecture | Status | Notes |
|--------------|--------|-------|
| x86-64 / x64 | ✅ Full | All features available |
| ARM64 / aarch64 | ✅ Full | Requires Harmony 2.4.2+, incl. Chinese search |

## Build

### Method 1: GitHub Actions (recommended)

Push code changes to GitHub and CI automatically builds & publishes a `build-N` release:

```bash
git add -A && git commit -m "..." && git push
```

Download the latest `StrmAssistant.dll` from [Releases](https://github.com/myp015/StrmAssistant/releases), deploy to `config/plugins/StrmAssistant.dll`, then restart Emby.

### Method 2: Local build

- Requires .NET 6.0 SDK or higher
- On Linux use `build-local.sh` (works around Resource.Embedder Linux path bug)

```bash
bash build-local.sh
```

Output: `StrmAssistant/bin/Release/StrmAssistant.merged.dll`

### Build libsimple.so (Chinese/Pinyin tokenizer)

Source & one-click script live in `StrmAssistant/Tokenizer/libsimple-src/` (see its README).
