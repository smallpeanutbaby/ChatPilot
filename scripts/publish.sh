#!/usr/bin/env bash
# 把服务器和节点发布成单文件程序（自带 .NET 运行时，目标电脑不需要安装 .NET）。
# 用法：scripts/publish.sh [输出目录]
#   输出目录默认是 artifacts/，结构为 <输出目录>/<平台>/{server,node}/
#   可以用环境变量 RIDS 指定平台，例如 RIDS="osx-arm64 win-x64"。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/artifacts}"
DOTNET="${DOTNET:-dotnet}"
RIDS="${RIDS:-osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64}"

for rid in $RIDS; do
  for project in server node; do
    name="$(tr '[:lower:]' '[:upper:]' <<< "${project:0:1}")${project:1}"
    echo "==> $rid / $project"
    "$DOTNET" publish "$ROOT/src/ChatPilot.$name" \
      -c Release -r "$rid" --self-contained true \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:DebugType=none \
      --nologo -v quiet \
      -o "$OUT/$rid/$project"
  done

  # Linux 服务器：把安装脚本、systemd 服务文件和 CPU 架构标记放在一起，整个目录拷到服务器上就能运行 install-server.sh。
  case "$rid" in
    linux-x64|linux-arm64)
      cp "$ROOT/deploy/linux/install-server.sh" "$ROOT/deploy/linux/chatpilot-server.service" "$OUT/$rid/server/"
      [ "$rid" = linux-x64 ] && echo x86_64 > "$OUT/$rid/server/ARCH" || echo aarch64 > "$OUT/$rid/server/ARCH"
      ;;
  esac
done

echo "完成：$OUT"
