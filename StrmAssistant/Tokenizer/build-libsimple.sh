#!/usr/bin/env bash
# =============================================================================
# build-libsimple.sh - 编译 libsimple.so（SQLite FTS5 中文+拼音分词器）
#
# 功能：编译 x86-64 和 arm64 两个架构的 libsimple.so，并输出到
#       StrmAssistant/Tokenizer/ 下供插件嵌入式资源使用。
#
# 为什么用 Ubuntu 18.04 容器编译：
#   Emby 容器运行在 glibc 2.27 环境（Debian buster）。宿主编译器（如 Debian
#   13 glibc 2.41）编出的 .so 需要更高 glibc，在 Emby 里会报
#   "GLIBC_2.38 not found"。Ubuntu 18.04 = glibc 2.27，产物与 Emby 完全兼容。
#
# 依赖：
#   - docker（本机 aarch64 可直接跑 arm64 容器；编译 amd64 需 qemu-user-static：
#     apt-get install -y qemu-user-static binfmt-support）
#   - cmake 3.19+（用于宿主侧验证，容器内脚本会自装）
#
# 用法：
#   bash StrmAssistant/Tokenizer/build-libsimple.sh            # 编译 arm64 + amd64
#   bash StrmAssistant/Tokenizer/build-libsimple.sh arm64      # 只编译 arm64
#   bash StrmAssistant/Tokenizer/build-libsimple.sh amd64      # 只编译 amd64
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
SRC_DIR="$SCRIPT_DIR/libsimple-src"
ARCHS="${1:-arm64 amd64}"
OUT_DIR="$SCRIPT_DIR"

BUILD_IMAGE="ubuntu:18.04"

build_in_docker() {
  local platform="$1"   # linux/arm64 或 linux/amd64
  local arch_label="$2" # arm64 或 amd64
  local out_path="$3"   # 产物输出路径

  echo "=========================================================="
  echo "==> 编译 $arch_label 版 libsimple.so (platform=$platform)"
  echo "=========================================================="

  docker run --rm --platform "$platform" \
    -v "$SRC_DIR":/src \
    -v "$OUT_DIR":/out \
    "$BUILD_IMAGE" bash -c "
      set -e
      export DEBIAN_FRONTEND=noninteractive
      apt-get update -qq >/dev/null 2>&1
      # 需要 cmake 3.19+（Ubuntu 18.04 自带 3.10 太旧），从 Kitware 下载静态版
      if ! cmake --version | grep -q '3\.\(19\|[2-9][0-9]\)' 2>/dev/null; then
        echo '==> 安装 cmake 3.31 (aarch64) ...'
        apt-get install -y -qq wget ca-certificates >/dev/null 2>&1
        case \"\$(uname -m)\" in
          aarch64) CMAKE_TAR=cmake-3.31.6-linux-aarch64.tar.gz ;;
          x86_64) CMAKE_TAR=cmake-3.31.6-linux-x86_64.tar.gz ;;
        esac
        wget -q https://github.com/Kitware/CMake/releases/download/v3.31.6/\$CMAKE_TAR -O /tmp/cmake.tgz
        mkdir -p /opt/cmake && tar -xzf /tmp/cmake.tgz -C /opt/cmake --strip-components=1
        export PATH=/opt/cmake/bin:\$PATH
      fi
      # 安装编译工具
      apt-get install -y -qq g++ make >/dev/null 2>&1
      echo '==> 工具链:' \$(g++ --version | head -1)

      rm -rf /src/build-\$arch_label
      mkdir -p /src/build-\$arch_label
      cd /src/build-\$arch_label
      cmake .. -DCMAKE_BUILD_TYPE=Release -DSIMPLE_WITH_JIEBA=ON -DBUILD_TEST_EXAMPLE=OFF > /tmp/cfg.log 2>&1
      make -j\$(nproc) > /tmp/make.log 2>&1
      echo '==> 编译完成'
      cp /src/build-\$arch_label/src/libsimple.so $out_path
      echo '==> 输出:' $out_path
      file $out_path
      echo '==> 所需最高 glibc:' \$(objdump -T $out_path | grep -oE 'GLIBC_[0-9.]+' | sort -V | tail -1)
    "
}

if [[ "$ARCHS" == *arm64* ]]; then
  build_in_docker "linux/arm64" "arm64" "/out/libsimple.arm64.so"
fi

if [[ "$ARCHS" == *amd64* ]]; then
  build_in_docker "linux/amd64" "amd64" "/out/libsimple.amd64.so"
fi

echo ""
echo "==================== 结果 ===================="
for f in "$OUT_DIR"/libsimple.arm64.so "$OUT_DIR"/libsimple.amd64.so; do
  if [ -f "$f" ]; then
    echo "  $f  ($(stat -c%s "$f") bytes)"
    file "$f"
  fi
done

# 产物是否放入插件资源目录由调用方决定（避免脚本误覆盖）
echo ""
echo "生成完毕。如需更新插件资源，请手动复制："
echo "  cp libsimple.arm64.so Tokenizer/linux-arm64/libsimple.so"
echo "  cp libsimple.amd64.so Tokenizer/linux/libsimple.so"
echo "（复制后需 push 触发 CI 重新打包 DLL）"
