#!/usr/bin/env bash
# ChatPilot 服务器：在 Linux（systemd）上一键安装或升级。
#
# 用法：把 chatpilot-server、appsettings.json、chatpilot-server.service 和本脚本放在同一个目录，然后：
#   sudo bash install-server.sh --public-url https://服务器IP:8443
# 可选：同一目录里放一份 state.json（从旧服务器的数据目录拷来），就会沿用旧的微信绑定和已配对的设备，不用重新扫码。
#
# 做的事：建 chatpilot 系统用户和目录 → 安装程序和服务 → 没绑定微信时在终端里扫码 → 放行防火墙 → 启动 → 打印证书指纹。
# 再次运行就是升级：替换程序、重启服务；已有的 appsettings.json、state.json 和证书都保留。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_DIR=/opt/chatpilot
DATA_DIR=/var/lib/chatpilot
SERVICE=chatpilot-server
PORT=8443
PUBLIC_URL=""

die() { echo "❌ $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --public-url) PUBLIC_URL="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) die "不认识的参数：$1（用 --help 查看用法）" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || die "请用 root 运行：sudo bash $0 --public-url https://服务器IP:$PORT"
case "$PUBLIC_URL" in
  https://*) ;;
  "") die "缺少 --public-url，例如：--public-url https://1.2.3.4:$PORT（节点用这个地址连服务器）" ;;
  *) die "--public-url 必须以 https:// 开头（服务器对外时必须开 TLS）" ;;
esac
command -v systemctl >/dev/null || die "没有 systemd，这个脚本只支持用 systemd 的发行版"
for f in chatpilot-server appsettings.json chatpilot-server.service; do
  [ -f "$HERE/$f" ] || die "找不到 $HERE/$f，请把它和本脚本放在同一个目录"
done

# 打包时会在同目录写一个 ARCH 文件（程序的 CPU 架构），不一致的话程序跑不起来。
if [ -f "$HERE/ARCH" ] && [ "$(cat "$HERE/ARCH")" != "$(uname -m)" ]; then
  die "这个安装包是 $(cat "$HERE/ARCH") 的，服务器是 $(uname -m)。请改用对应平台的包（linux-x64 或 linux-arm64）"
fi

step "准备 chatpilot 用户和目录"
if ! id chatpilot >/dev/null 2>&1; then
  useradd --system --home "$DATA_DIR" --shell "$(command -v nologin || echo /usr/sbin/nologin)" chatpilot
fi
mkdir -p "$APP_DIR" "$DATA_DIR"
chown chatpilot: "$DATA_DIR"
chmod 700 "$DATA_DIR"

if systemctl is-active --quiet "$SERVICE"; then
  step "停止正在运行的服务（升级）"
  systemctl stop "$SERVICE"
fi

step "安装程序"
install -m 755 "$HERE/chatpilot-server" "$APP_DIR/chatpilot-server"
if [ -f "$APP_DIR/appsettings.json" ]; then
  echo "保留已有的 $APP_DIR/appsettings.json"
else
  install -m 644 "$HERE/appsettings.json" "$APP_DIR/appsettings.json"
fi

if [ -f "$HERE/state.json" ]; then
  if [ -f "$DATA_DIR/state.json" ]; then
    echo "⚠️ $DATA_DIR/state.json 已存在，没有覆盖（要换成新的请先手动删掉它）"
  else
    install -o chatpilot -g chatpilot -m 600 "$HERE/state.json" "$DATA_DIR/state.json"
    echo "已导入 state.json：沿用旧的微信绑定和已配对的设备"
  fi
fi

step "安装 systemd 服务"
# 去掉 CR：在 Windows 上打的包可能带 CRLF 换行，systemd 会把行尾的 \r 当成路径的一部分。
tr -d '\r' < "$HERE/chatpilot-server.service" \
  | sed "s#^Environment=ChatPilot__PublicUrl=.*#Environment=ChatPilot__PublicUrl=$PUBLIC_URL#" \
  > "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload

run_as_chatpilot() { runuser -u chatpilot -- env ChatPilot__DataDir="$DATA_DIR" "$APP_DIR/chatpilot-server" "$@"; }

if ! grep -q '"botToken"' "$DATA_DIR/state.json" 2>/dev/null; then
  step "绑定微信：用手机微信扫描下面的二维码，并在手机上确认"
  run_as_chatpilot login || die "微信绑定没有完成，可以再运行一次本脚本重试"
fi

step "放行防火墙端口 $PORT/tcp"
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  ufw allow "$PORT/tcp" >/dev/null && echo "ufw 已放行 $PORT/tcp"
elif command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
  firewall-cmd --quiet --permanent --add-port="$PORT/tcp" && firewall-cmd --quiet --reload && echo "firewalld 已放行 $PORT/tcp"
else
  echo "本机没有启用 ufw / firewalld，跳过"
fi
echo "别忘了在云服务器控制台的安全组里也放行 $PORT/tcp"

step "启动服务"
systemctl enable --now "$SERVICE" >/dev/null
for _ in $(seq 1 30); do
  if curl -fsk "https://127.0.0.1:$PORT/healthz" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
curl -fsk "https://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || die "服务没有正常启动，看日志：journalctl -u $SERVICE -n 50 --no-pager"

FINGERPRINT="$(run_as_chatpilot fingerprint 2>/dev/null | grep -o 'sha256:[0-9A-Fa-f:]*' | head -n 1 || true)"

echo
echo "✅ ChatPilot 服务器已经在运行：$PUBLIC_URL"
echo "   证书指纹：${FINGERPRINT:-（没取到，运行 sudo -u chatpilot env ChatPilot__DataDir=$DATA_DIR $APP_DIR/chatpilot-server fingerprint 查看）}"
echo "   日志：journalctl -u $SERVICE -f"
echo
echo "下一步：在微信里发 /pair 设备名，把返回的命令拿到要控制的电脑上运行。"
if [ -f "$HERE/state.json" ]; then
  echo "注意：这是从旧服务器迁移过来的。旧服务器要停掉（两个服务器同时收微信消息会互相抢），"
  echo "      原来配对过的电脑要把节点配置里的 serverUrl 改成 $PUBLIC_URL、serverFingerprint 改成上面的指纹，再重启节点。"
  echo "      安装包里的 state.json 含有微信 bot token，确认迁移成功后请删掉它：rm -f $HERE/state.json"
fi
