# Emby Strm Assistant

![logo](StrmAssistant/Properties/thumb.png "logo")

## Community Edition - AI-Optimized for Latest Emby

## [[中文]](README.md)

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
