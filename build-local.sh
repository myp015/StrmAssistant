#!/usr/bin/env bash
# Local Linux build for StrmAssistant.
# Workarounds:
#  1. Resource.Embedder 2.2.0 has a Linux bug (backslash paths) -> temporarily removed during build.
#     Satellite zh/zh-hant resource DLLs are copied alongside instead (Emby loads them).
#  2. ILRepack.exe is a .NET Framework exe -> run via `dotnet ILRepack.exe` (has net6.0 runtimeconfig).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
ROOT="$SCRIPT_DIR"
PROJ="$ROOT/StrmAssistant"
CONFIG="${1:-Release}"
TFM=net6.0
OUT="$PROJ/bin/$CONFIG/$TFM"

export PATH="$PATH:/root/.dotnet"

echo "==> Building (embedder + PostBuildMerge temporarily removed) ..."
CS="$PROJ/StrmAssistant.csproj"
cp "$CS" "$CS.bak"
python3 - "$CS" <<'PYEOF'
import re, sys
p = sys.argv[1]
s = open(p).read()
s = s.replace('    <PackageReference Include="Resource.Embedder" Version="2.2.0" />\n', '')
s = re.sub(r'  <Target Name="PostBuildMerge".*?</Target>\n', '', s, flags=re.S)
open(p, 'w').write(s)
PYEOF

build_ok=0
if dotnet build "$CS" -c "$CONFIG" -p:StrmAssistantPluginOutputDir= 2>&1 \
        | grep -qE "Build succeeded|error CS|error MSB"; then
  :
fi

# Build again capturing status properly
if dotnet build "$CS" -c "$CONFIG" -p:StrmAssistantPluginOutputDir= > /tmp/strmbuild.log 2>&1; then
  build_ok=1
else
  echo "ERROR: dotnet build failed (see /tmp/strmbuild.log)" >&2
fi

mv "$CS.bak" "$CS"

if [ "$build_ok" != "1" ]; then
  echo "BUILD FAILED - aborting (merge skipped)" >&2
  tail -15 /tmp/strmbuild.log >&2
  exit 1
fi

# NOTE: the PostBuildMerge ILRepack step inside csproj will run and may fail on Linux
# (ILRepack.exe Exec format error) - that's expected; we do our own merge below.

RAW="$OUT/StrmAssistant.dll"
if [ ! -f "$RAW" ]; then
  echo "ERROR: no build output $RAW" >&2
  exit 1
fi
echo "==> Merging dependencies via ILRepack (dotnet) ..."
ILREPACK="/root/.nuget/packages/ilrepack/2.0.42/tools/ILRepack.exe"
MERGED="$PROJ/bin/$CONFIG/StrmAssistant.merged.dll"
rm -f "$MERGED"
dotnet "$ILREPACK" /out:"$MERGED" \
    "$RAW" \
    "$OUT/0Harmony.dll" \
    "$OUT/ChineseConverter.dll" \
    "$OUT/TinyPinyin.dll" \
    /lib:"$OUT" > /tmp/ilrepack.log 2>&1 || { echo "ILRepack failed:"; cat /tmp/ilrepack.log; exit 1; }

echo "==> Merged -> $MERGED ($(stat -c%s "$MERGED") bytes)"

# Copy satellite resource DLLs alongside (replaces embedder's role)
mkdir -p "$OUT/zh" "$OUT/zh-hant"
cp -f "$OUT/zh/StrmAssistant.resources.dll" "$OUT/zh/" 2>/dev/null || true
cp -f "$OUT/zh-hant/StrmAssistant.resources.dll" "$OUT/zh-hant/" 2>/dev/null || true

echo "DONE"
