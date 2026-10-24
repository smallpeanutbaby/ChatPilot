#!/usr/bin/env bash
# ChatPilot 服务器：在 Linux（systemd）上一键安装或升级。
#
# 用法：把 chatpilot-server、appsettings.json、chatpilot-server.service 和本脚本放在同一个目录，然后：
#   sudo bash install-server.sh --public-url https://服务器IP:8443 [--port 8443]
# 迁移：同一目录里再放旧服务器数据目录里的 state.json（以及 server.pfx），就会沿用旧的微信绑定和已配对的设备
# （以及证书指纹），不用重新扫码、重新配对。
#
# 做的事：检查环境 → 建 chatpilot 系统用户和目录 → 安装程序和服务 → 没绑定微信时在终端里扫码 → 放行防火墙 → 启动 → 打印证书指纹。
# 再次运行就是升级：替换程序、重启服务；已有的 appsettings.json、state.json 和证书都保留，服务文件每次按参数重新生成。
set -euo pipefail

# 用 su（不带 -）切到 root 时 PATH 里可能没有 /usr/sbin，useradd、runuser 会找不到。
PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH"

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_DIR=/opt/chatpilot
DATA_DIR=/var/lib/chatpilot
SERVICE=chatpilot-server
UNIT="/etc/systemd/system/$SERVICE.service"
DROPIN_DIR="$UNIT.d"
PORT=8443
PUBLIC_URL=""

die() { echo "❌ $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --public-url|--port)
      [ $# -ge 2 ] || die "$1 后面要跟一个值（用 --help 查看用法）"
      if [ "$1" = --port ]; then PORT="$2"; else PUBLIC_URL="$2"; fi
      shift 2
      ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) die "不认识的参数：$1（用 --help 查看用法）" ;;
  esac
done

# ---------- 检查：全部通过之前不改动系统 ----------

[ "$(id -u)" -eq 0 ] || die "请用 root 运行：sudo bash $0 --public-url https://服务器IP:$PORT"

[[ "$PORT" =~ ^[0-9]{1,5}$ ]] && [ "$PORT" -ge 1 ] && [ "$PORT" -le 65535 ] || die "--port 必须是 1–65535 之间的数字"

# 地址会写进 systemd 服务文件，只接受"https:// + 域名或 IP + 可选端口"，杜绝空格、换行等会被 systemd 另作解释的字符。
URL_PATTERN='^https://([A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])(:[0-9]{1,5})?/?$'
[ -n "$PUBLIC_URL" ] || die "缺少 --public-url，例如：--public-url https://1.2.3.4:$PORT（节点用这个地址连服务器）"
[[ "$PUBLIC_URL" =~ $URL_PATTERN ]] || die "--public-url 格式不对：只能是 https:// 加域名或 IP，可带端口，例如 https://1.2.3.4:$PORT"

command -v systemctl >/dev/null || die "没有 systemd，这个脚本只支持用 systemd 的发行版"
for f in chatpilot-server appsettings.json chatpilot-server.service; do
  [ -f "$HERE/$f" ] || die "找不到 $HERE/$f，请把它和本脚本放在同一个目录"
done

# 打包时会在同目录写一个 ARCH 文件（程序的 CPU 架构），不一致的话程序跑不起来。
if [ -f "$HERE/ARCH" ] && [ "$(tr -d '\r\n' < "$HERE/ARCH")" != "$(uname -m)" ]; then
  die "这个安装包是 $(tr -d '\r\n' < "$HERE/ARCH") 的，服务器是 $(uname -m)。请改用对应平台的包（linux-x64 或 linux-arm64）"
fi

# 服务器程序需要系统的 ICU 库（处理中文等文字），最小安装的系统可能没有，缺了的话程序一启动就崩溃。
if command -v ldconfig >/dev/null && ! ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so'; then
  die "系统缺少 ICU 库。先安装：Debian/Ubuntu 用 apt-get install -y libicu-dev，CentOS/阿里云 Linux 用 yum install -y libicu，然后重新运行本脚本"
fi

# 健康检查用 curl 或 wget（自签证书，跳过校验）；都没有时只看服务状态。
HTTP_TOOL=""
if command -v curl >/dev/null; then HTTP_TOOL=curl; elif command -v wget >/dev/null; then HTTP_TOOL=wget; fi
http_ok() {
  case "$HTTP_TOOL" in
    curl) curl -fsk --max-time 5 "$1" >/dev/null 2>&1 ;;
    wget) wget -q --no-check-certificate --timeout=5 -O /dev/null "$1" ;;
  esac
}

# 先在临时目录里把服务文件准备好，确认无误再替换，避免写到一半留下空文件。
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
# 去掉 CR：在 Windows 上打的包可能带 CRLF 换行，systemd 会把行尾的 \r 当成路径的一部分。
tr -d '\r' < "$HERE/chatpilot-server.service" | grep -v '^Environment=ChatPilot__PublicUrl=' > "$TMP/unit"
grep -q '^ExecStart=' "$TMP/unit" || die "$HERE/chatpilot-server.service 不完整（没有 ExecStart）"
printf '[Service]\nEnvironment=ChatPilot__PublicUrl=%s\n' "$PUBLIC_URL" > "$TMP/public-url.conf"

# ---------- 安装 ----------

step "准备 chatpilot 用户和目录"
if ! id chatpilot >/dev/null 2>&1; then
  useradd --system --home "$DATA_DIR" --shell "$(command -v nologin || echo /usr/sbin/nologin)" chatpilot
fi
# 显式给权限，不受 umask 影响：程序目录只有 root 能写，数据目录只有 chatpilot 能进。
install -d -m 755 -o root -g root "$APP_DIR"
install -d -m 700 -o chatpilot -g chatpilot "$DATA_DIR"

IMPORTED=""
for f in state.json server.pfx; do
  [ -f "$HERE/$f" ] || continue
  if [ -f "$DATA_DIR/$f" ]; then
    echo "⚠️ $DATA_DIR/$f 已存在，没有覆盖（要换成新的请先手动删掉它）"
  else
    install -m 600 -o chatpilot -g chatpilot "$HERE/$f" "$DATA_DIR/$f"
    IMPORTED="$IMPORTED $f"
  fi
  chmod 600 "$HERE/$f"   # 安装包里的这份含有密钥，别让其他用户读到
done
[ -z "$IMPORTED" ] || echo "已导入：$IMPORTED"

WAS_RUNNING=""
if systemctl is-active --quiet "$SERVICE"; then
  WAS_RUNNING=1
  trap 'rm -rf "$TMP"; [ -z "$WAS_RUNNING" ] || echo "⚠️ 升级中途失败，服务现在是停止状态。修好问题后重新运行本脚本，或者先 systemctl start $SERVICE 用回原来的版本" >&2' EXIT
  step "停止正在运行的服务（升级）"
  systemctl stop "$SERVICE"
fi

step "安装程序和服务"
install -m 755 -o root -g root "$HERE/chatpilot-server" "$APP_DIR/chatpilot-server"
if [ -f "$APP_DIR/appsettings.json" ]; then
  echo "保留已有的 $APP_DIR/appsettings.json"
else
  install -m 644 -o root -g root "$HERE/appsettings.json" "$APP_DIR/appsettings.json"
fi
install -m 644 "$TMP/unit" "$UNIT"
install -d -m 755 "$DROPIN_DIR"
install -m 644 "$TMP/public-url.conf" "$DROPIN_DIR/public-url.conf"
systemctl daemon-reload

run_as_chatpilot() { runuser -u chatpilot -- env ChatPilot__DataDir="$DATA_DIR" "$APP_DIR/chatpilot-server" "$@"; }

if ! grep -q '"botToken"' "$DATA_DIR/state.json" 2>/dev/null; then
  step "绑定微信：用手机微信扫描下面的二维码，并在手机上确认"
  run_as_chatpilot login || die "微信绑定没有完成，可以再运行一次本脚本重试"
fi

step "放行防火墙端口 $PORT/tcp"
if command -v ufw >/dev/null && LC_ALL=C ufw status 2>/dev/null | grep -q '^Status: active'; then
  ufw allow "$PORT/tcp" >/dev/null && echo "ufw 已放行 $PORT/tcp"
elif command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
  firewall-cmd --quiet --permanent --add-port="$PORT/tcp" && firewall-cmd --quiet --reload && echo "firewalld 已放行 $PORT/tcp"
else
  echo "本机没有启用 ufw / firewalld，跳过"
fi
echo "别忘了在云服务器控制台的安全组里也放行 $PORT/tcp"

step "启动服务"
systemctl enable --now "$SERVICE" >/dev/null
WAS_RUNNING=""
HEALTH="https://127.0.0.1:$PORT/healthz"
FAILED="服务没有正常启动。看日志：journalctl -u $SERVICE -n 50 --no-pager（如果 appsettings.json 里的端口不是 $PORT，用 --port 指定）"
if [ -n "$HTTP_TOOL" ]; then
  healthy=""
  for _ in $(seq 1 30); do
    http_ok "$HEALTH" && { healthy=1; break; }
    sleep 1
  done
  [ -n "$healthy" ] || die "$FAILED"
else
  sleep 5
  systemctl is-active --quiet "$SERVICE" || die "$FAILED"
  echo "（没有 curl / wget，没法访问 $HEALTH 做健康检查；服务状态是运行中）"
fi

FINGERPRINT="$(run_as_chatpilot fingerprint 2>/dev/null | grep -o 'sha256:[0-9a-f]*' | head -n 1 || true)"

echo
echo "✅ ChatPilot 服务器已经在运行：$PUBLIC_URL"
echo "   证书指纹：${FINGERPRINT:-（没取到，运行 sudo -u chatpilot env ChatPilot__DataDir=$DATA_DIR $APP_DIR/chatpilot-server fingerprint 查看）}"
echo "   日志：journalctl -u $SERVICE -f"
echo
echo "下一步：在微信里发 /pair 设备名，把返回的命令拿到要控制的电脑上运行。"
if [[ "$IMPORTED" == *state.json* ]]; then
  echo
  echo "这是从旧服务器迁移过来的："
  echo "  1. 旧服务器要停掉：两个服务器同时收微信消息会互相抢。"
  echo "  2. 原来配对过的电脑不用重新配对：把节点配置（~/.chatpilot/node.json）里的 serverUrl 改成 $PUBLIC_URL，"
  if [[ "$IMPORTED" == *server.pfx* ]]; then
    echo "     证书也迁移过来了，指纹没变；改完重启节点。"
  else
    echo "     serverFingerprint 改成上面的指纹，再重启节点。"
  fi
  echo "  3. 安装包目录里的 state.json$([[ "$IMPORTED" == *server.pfx* ]] && echo ' 和 server.pfx') 含有密钥，确认迁移成功后删掉：rm -f \"$HERE/state.json\" \"$HERE/server.pfx\""
fi
