# ChatPilot

**用一部手机，通过微信指挥你所有电脑上的 AI 编码 agent。**

*Command the AI coding agents (Claude Code, Codex, …) on all your computers from WeChat on your phone.*

[![CI](https://github.com/smallpeanutbaby/ChatPilot/actions/workflows/ci.yml/badge.svg)](https://github.com/smallpeanutbaby/ChatPilot/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/smallpeanutbaby/ChatPilot)](https://github.com/smallpeanutbaby/ChatPilot/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Platforms](https://img.shields.io/badge/node-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)

人不在电脑前，也能在微信里给家里、公司每台电脑上的 Claude Code、Codex 派活：看它们在干什么、批准确认框、收到做完的结果。

```text
你：/ls
🤖 📱 mac（当前）
     1. 💤 tideq · claude · 空闲
     2. ⏳ web · codex · 工作中
   📱 win
     3. ⚠️ api · claude · 等你确认

你：@tideq 把登录页的报错修了，跑一下测试
🤖 📨 已发给 [mac] tideq，做完会通知你
   …
🤖 ✅ [mac] tideq 做完了：
   改好了：登录失败时会显示具体原因，相关的 12 个测试全部通过。

你：/yes win/api
🤖 👌 [win] api：已选择：1. Yes
```

## 目录

- [特点](#特点)
- [工作原理](#工作原理)
- [准备工作](#准备工作)
- [快速开始](#快速开始)：一台电脑先跑起来
- [正式部署](#正式部署)：云服务器 + 多台电脑，升级、迁移、卸载
- [使用指南](#使用指南)：每条指令怎么用
- [支持的 agent](#支持的-agent)
- [配置](#配置)
- [安全](#安全)
- [排错](#排错)
- [常见问题](#常见问题)
- [已知限制](#已知限制)
- [开发](#开发) · [贡献](#贡献) · [致谢](#致谢) · [许可证](#许可证)

## 特点

- **一个微信管所有电脑**：每台电脑装一个节点，在同一个对话里用 `@mac/web`、`@win/api` 指定是哪台的哪个 agent。
- **没有额外的 AI**：理解指令、写代码的都是你电脑上原来的 agent；ChatPilot 只负责传话，不调用任何大模型接口，也不需要你提供 API Key。
- **支持 20 多种 agent**：Claude Code、Codex、pi、Kimi、Qwen、opencode、Cursor、Copilot……herdr 能管的都能用（[支持细节](docs/AGENTS.md)）。
- **结果自动推回来**：任务做完推送 agent 的回复，卡在确认框时推送屏幕内容；能读到会话记录时推的是完整原文，不是终端截屏。电脑掉线、恢复也会通知你。
- **确认框不乱点**：`/yes` 只会选"这一次同意"，绝不替你选"以后都允许""本项目都允许"这类会扩大权限的选项；也只同意发给你看过的那个确认框，不会替你同意没看过的。
- **忙的时候排队**：agent 在干活时再派活会自动排队，做完一条发下一条，不会把文字敲进正在干活的终端。
- **离开电脑也不漏**：`/watch` 盯着在电脑上开的长任务，做完或卡在确认框照样通知；`/at` 定时派活；`/wake` 唤醒睡着的电脑。
- **能发截图**：先发图片再派活，报错截图、设计稿会一起交给 agent。
- **有备用通道**：可以再接一个 Telegram 机器人，微信那边出问题时照样能用。
- **电脑不用开端口**：节点只往外连服务器，不需要公网 IP；连接走 TLS，节点只认配对时记下的证书指纹。
- **单文件程序**：服务器和节点都打包成自带运行时的单个可执行文件，目标电脑不用装 .NET。

## 工作原理

```text
手机微信 ──► 腾讯 iLink ◄── ChatPilot 服务器（一份，放云上或任意一台电脑）
                                  ▲  WebSocket（TLS + 证书指纹校验）
                     ┌────────────┴────────────┐
               ChatPilot 节点（Mac）      ChatPilot 节点（Windows）…
                     │                         │
                   herdr                     herdr
                     │                         │
          Claude Code、Codex…        Claude Code、Codex…
```

| 组件 | 做什么 | 装在哪 |
|---|---|---|
| **服务器** `chatpilot-server` | 绑定微信、收你的消息、解析指令、转发给对应电脑、把结果推回微信 | 只要一份：云服务器，或者只有一台电脑时直接放在这台电脑上 |
| **节点** `chatpilot-node` | 执行服务器发来的指令：列出 agent、派活、读终端、按确认键、新开 agent | 每台要被控制的电脑一份 |
| **[herdr](https://herdr.dev)** | 终端里的 agent 管理器：开终端、识别 agent 在工作还是在等确认、读写终端 | 每台要被控制的电脑 |

**一条消息的旅程**（以 `@tideq 跑一下测试` 为例）：

1. 服务器从腾讯 iLink 长轮询收到你的消息，确认是绑定的那个微信号发的；
2. 服务器解析出目标 `tideq` 在哪台电脑上，通过这台电脑的节点连接发过去；
3. 节点校验参数，调用 `herdr agent prompt` 把文字交给 agent，立刻回"已发送"；
4. 节点在后台盯着 agent 的状态：做完了、卡在确认框了、出错了，都会作为事件发回服务器；
5. 做完时节点读 agent 的会话记录（读不到就读终端），把最后一条回复发回服务器，服务器推到你微信。

扫码绑定微信是服务器做的，只做一次；以后加电脑只装节点、配对，不用再扫码。

## 准备工作

- **每台要被控制的电脑**：
  - 装好 [herdr](https://herdr.dev) 并打开过一次；
  - 装好你要用的 agent（Claude Code、Codex……），并在终端里手动运行过一次，确认已经登录；
  - 给常用的 agent 装上 herdr 集成，推送的回复才完整：`herdr integration install claude`、`herdr integration install codex`……用 `herdr integration status` 查看。
- **一个微信号**：用来绑定机器人、接收通知。
- **放服务器的地方**：一台云服务器（1 核 1G 就够，香港或海外不用备案）；只有一台电脑时也可以直接放在这台电脑上。**不需要域名，也不需要买证书。**
- **网络**：服务器要能访问 `https://ilinkai.weixin.qq.com`（收发消息）和 `https://novac2c.cdn.weixin.qq.com`（下载你发的图片）；每台电脑要能访问服务器的 8443 端口。
- **程序**：从 [Releases](https://github.com/smallpeanutbaby/ChatPilot/releases/latest) 下载对应系统的压缩包，解压就能用，不用装 .NET。想自己打包的话需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)，见[自己打包](#自己打包)。

> **为什么一定要 herdr？** 节点不直接操作终端，而是通过 herdr：开终端、判断 agent 是在工作还是在等确认、读写终端、拿到会话记录。这些对 20 多种 agent 都要单独适配，herdr 已经做好了。不装 herdr 的话，节点什么也做不了。

## 快速开始

先在一台电脑上把服务器和节点都跑起来，不用云服务器。下面以 Windows 为例，macOS / Linux 同理（自启方式见[正式部署](#正式部署)）。

**1. 下载**：从 [Releases](https://github.com/smallpeanutbaby/ChatPilot/releases/latest) 下载 `chatpilot-server-win-x64.zip` 和 `chatpilot-node-win-x64.zip`，把解压出来的两个文件夹里的内容分别放到 `C:\Tools\chatpilot\server\` 和 `C:\Tools\chatpilot\node\`（位置随意，下面以它为例）。程序旁边已经带好了开机自启脚本。

**2. 配置服务器只听本机**：修改 `C:\Tools\chatpilot\server\appsettings.json`：

```json
"ChatPilot": {
  "ListenAddress": "127.0.0.1",
  "PublicUrl": "http://127.0.0.1:8443",
  "Tls": { "Enabled": false }
}
```

只听本机时可以不开 TLS；服务器要对外时必须开（默认就是开的，程序也会拒绝"对外监听却不开 TLS"的配置）。

**3. 绑定微信**

```powershell
C:\Tools\chatpilot\server\chatpilot-server.exe login
```

终端里会显示二维码，用手机微信扫码并在手机上确认，看到"✅ 微信绑定成功"就好了。之后微信里会出现和这个机器人的对话，以后都在这个对话里发指令。如果手机上要求输入数字验证码，直接在这个终端里输入。

**4. 让服务器常驻**（登录 Windows 后自动启动）

```powershell
powershell -ExecutionPolicy Bypass -File C:\Tools\chatpilot\server\install-server-task.ps1 -ServerPath C:\Tools\chatpilot\server\chatpilot-server.exe
```

**5. 配对节点**：在微信里发 `/pair win`，把返回的命令拿到这台电脑上运行，加上允许 `/new` 打开的目录：

```powershell
C:\Tools\chatpilot\node\chatpilot-node.exe pair --server http://127.0.0.1:8443 --code ABCD-EFGH --name win --root D:\code
C:\Tools\chatpilot\node\chatpilot-node.exe doctor
powershell -ExecutionPolicy Bypass -File C:\Tools\chatpilot\node\install-node-task.ps1 -NodePath C:\Tools\chatpilot\node\chatpilot-node.exe
```

**6. 试一下**：微信里发 `/devices`，看到 `🟢 win` 就通了；发 `/ls` 列出 agent，`@序号 内容` 派活。完整用法见[使用指南](#使用指南)。

> Windows 上用 npm 装的 codex、kimi、pi 是通过 `.ps1` 脚本启动的。如果 `/new` 启动它们时超时，执行一次 `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`。
>
> 程序没有代码签名。双击运行时如果弹出"Windows 已保护你的电脑"，或者计划任务脚本被拦住，先解除下载标记：`Get-ChildItem C:\Tools\chatpilot -Recurse | Unblock-File`。

## 正式部署

要控制多台电脑、或者人在外面也要用时，把服务器放到云上，每台电脑只装节点。

### 服务器（Linux 云服务器）

安装包里带了安装脚本 [`install-server.sh`](deploy/linux/install-server.sh)，在服务器上下载、解压、运行：

```bash
# ARM 服务器把下面两处 x64 换成 arm64
curl -fLO https://github.com/smallpeanutbaby/ChatPilot/releases/latest/download/chatpilot-server-linux-x64.tar.gz
tar -xzf chatpilot-server-linux-x64.tar.gz
sudo bash chatpilot-server-linux-x64/install-server.sh --public-url https://服务器IP:8443
```

服务器访问不了 GitHub 的话，在自己电脑上下载好，用 `scp` 传上去再解压；或者[自己打包](#自己打包)，把 `artifacts/linux-x64/server/` 整个目录传上去（目录里同样带着安装脚本）。

脚本会：检查环境（CPU 架构、ICU 库、参数格式）→ 建 `chatpilot` 系统用户和目录 → 安装程序和 systemd 服务 → 没绑定过微信时在终端里显示二维码让你扫 → 用 ufw / firewalld 放行端口 → 启动并做健康检查 → 打印**证书指纹**（`sha256:…`，节点配对时要用）。

- 再次运行就是升级：替换程序、重启服务；已有的 `appsettings.json`、微信绑定和证书都保留。每次都要带上 `--public-url`，服务文件会按它重新生成。
- `appsettings.json` 里改过端口的话，加 `--port 端口`，健康检查和防火墙都用它。
- 服务器程序需要系统的 ICU 库，大多数发行版自带；最小安装的系统缺的话脚本会提示安装命令。

<details>
<summary>不用脚本，手动安装</summary>

```bash
cd chatpilot-server-linux-x64        # 解压出来的安装包目录
sudo useradd --system --home /var/lib/chatpilot --shell /usr/sbin/nologin chatpilot
sudo mkdir -p /opt/chatpilot /var/lib/chatpilot && sudo chown chatpilot: /var/lib/chatpilot && sudo chmod 700 /var/lib/chatpilot
sudo cp chatpilot-server appsettings.json /opt/chatpilot/ && sudo chmod +x /opt/chatpilot/chatpilot-server
sudo -u chatpilot env ChatPilot__DataDir=/var/lib/chatpilot /opt/chatpilot/chatpilot-server login   # 扫码绑定微信

# 把 service 文件里的 ChatPilot__PublicUrl 改成 https://服务器IP:8443，再注册成服务
sudo cp chatpilot-server.service /etc/systemd/system/ && sudo systemctl daemon-reload
sudo systemctl enable --now chatpilot-server
journalctl -u chatpilot-server -f    # 看日志；记下打印出来的证书指纹 sha256:…
```

</details>

- 安全组 / 防火墙只放行 **8443/tcp**（和你的 SSH 端口）；SSH 建议只允许密钥登录。
- **证书不用买**：第一次启动时自动生成自签证书（10 年有效），保存在数据目录的 `server.pfx`。节点配对时记下它的指纹，以后只认这张证书。随时可以查看指纹：`sudo -u chatpilot env ChatPilot__DataDir=/var/lib/chatpilot /opt/chatpilot/chatpilot-server fingerprint`（要带上数据目录，否则它会在别处新生成一张证书，打出来的指纹不是服务正在用的）。
- service 文件已经做了加固：以 `chatpilot` 用户运行、系统目录只读、只能写数据目录、不能提权。
- 香港或海外服务器不用备案；国内服务器用域名加 443 端口需要 ICP 备案。

### 节点：每台电脑

先从 [Releases](https://github.com/smallpeanutbaby/ChatPilot/releases/latest) 下载这台电脑对应的节点并解压：

| 系统 | 文件 |
|---|---|
| Windows | `chatpilot-node-win-x64.zip` |
| macOS（Apple 芯片） | `chatpilot-node-osx-arm64.tar.gz` |
| macOS（Intel） | `chatpilot-node-osx-x64.tar.gz` |
| Linux | `chatpilot-node-linux-x64.tar.gz`、`chatpilot-node-linux-arm64.tar.gz` |

程序没有代码签名。macOS 上用浏览器下载的，第一次运行会被系统拦住，先解除隔离：`xattr -d com.apple.quarantine chatpilot-node`（用 `curl` 下载的没有这个问题）。

然后无论哪个系统都一样：在微信里发 `/pair 设备名`，复制返回的命令在那台电脑上运行（命令里已经带好了服务器地址、配对码和证书指纹），再加上 `--root` 指定允许 `/new` 打开的目录：

```bash
chatpilot-node pair --server https://服务器IP:8443 --code ABCD-EFGH \
  --fingerprint sha256:… --name mac --root ~/code --root ~/work
chatpilot-node doctor     # 自检：版本、herdr、允许的目录、agent 类型、服务器连通性
```

`--root` 可以写多个；不写默认是用户主目录。配对码 10 分钟内有效、只能用一次。配置写在 `~/.chatpilot/node.json`（权限 600）；正在跟踪的任务记在旁边的 `node.tasks.json`（同样是 600，里面有你交给 agent 的文字），节点重启后靠它接着跟踪。

然后设成开机自启：

下面的命令都在解压出来的节点目录里运行，自启用的文件就在程序旁边。

同一份配置同时只能运行一个节点（配置旁边的 `node.lock` 管着）：已经有一个在运行时，再运行 `chatpilot-node run` 会等 20 秒，等不到就退出。它以退出码 0 退出，开机自启工具不会反复把它拉起来；要重启节点，先停掉正在运行的那个。

**Windows**：登录后通过计划任务启动。不要注册成 Windows 服务：服务账户访问不到你的 herdr。

```powershell
powershell -ExecutionPolicy Bypass -File C:\Tools\chatpilot\node\install-node-task.ps1 -NodePath C:\Tools\chatpilot\node\chatpilot-node.exe
# 日志：%LOCALAPPDATA%\ChatPilot\node.log
# 卸载：Unregister-ScheduledTask -TaskName ChatPilotNode -Confirm:$false
```

**macOS**：launchd，自带 `caffeinate`，节点运行期间电脑不会进入空闲睡眠。

```bash
sudo cp chatpilot-node /usr/local/bin/
# 把 com.chatpilot.node.plist 里的 YOUR_NAME 换成你的用户名
cp com.chatpilot.node.plist ~/Library/LaunchAgents/
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.chatpilot.node.plist
tail -f ~/Library/Logs/chatpilot-node.log                      # 日志
launchctl bootout gui/$(id -u)/com.chatpilot.node              # 停止
```

**Linux**：用户级 systemd 服务，以你自己的身份运行。

```bash
cp chatpilot-node ~/.local/bin/
mkdir -p ~/.config/systemd/user && cp chatpilot-node.service ~/.config/systemd/user/
systemctl --user daemon-reload && systemctl --user enable --now chatpilot-node
loginctl enable-linger $USER                  # 没登录桌面时也保持运行
journalctl --user -u chatpilot-node -f        # 日志
```

### 升级

配置、微信绑定、配对关系都在数据目录里，升级不受影响。`/devices` 会显示每台电脑的节点版本。

- 节点：在那台电脑上运行 `chatpilot-node update`（Windows 上是 `C:\Tools\chatpilot\node\chatpilot-node.exe update`）。它会从 GitHub 下载最新版，核对校验值，在这台电脑上试运行一次，然后替换程序；按上面的方式设了开机自启、节点正在运行的，会顺便重启节点（Windows 上会一起结束停掉计划任务后留下来的旧节点进程），正在等的任务接着跟踪。你自己停掉的节点不会被打开，只提示怎么启动；重启失败时命令以非 0 退出，并告诉你怎么手动重启。`--check` 只看有没有新版本。程序放在 `/usr/local/bin` 这类需要管理员权限的目录时，用 `sudo chatpilot-node update`，重启的仍是你自己账号下的服务。同一个程序同时只能有一个 `update` 在升级。v0.1.0 还没有这个命令，从 0.1.0 升级需要按下面的方式手动换一次。
- 服务器：按[上面的方式](#服务器linux-云服务器)下载、解压新版本，再运行一次 `install-server.sh`，同样带上 `--public-url`；或者手动 `sudo systemctl stop chatpilot-server` → 替换 `/opt/chatpilot/chatpilot-server` → `sudo systemctl start chatpilot-server`。用 `scp` 上传目录的话，先删掉服务器上的旧目录，否则新目录会被套进旧目录里。
- 不用 `update` 也可以手动换：下载新版本（或重新打包），停掉节点、替换可执行文件、再启动。Windows 用 `Stop-ScheduledTask -TaskName ChatPilotNode`，再结束留下来的节点进程（`Get-Process chatpilot-node | Stop-Process -Force`：`Stop-ScheduledTask` 只结束外面的 cmd），替换后 `Start-ScheduledTask -TaskName ChatPilotNode`；macOS 用 `launchctl kickstart -k gui/$(id -u)/com.chatpilot.node`，Linux 用 `systemctl --user restart chatpilot-node`。

服务器和节点之间的协议有版本号，升级时尽量两边一起升。新指令（`/diff`、`/stop`、`/close`、`/watch`、`/wake`、排队、图片）需要节点也升级；节点还是旧版时，这些指令会提示"请升级节点"。从旧版升级的服务器，`appsettings.json` 会保留原样，建议手动加上 `"TimeZone": "Asia/Shanghai"`（或你所在的时区），否则 `/at 23:00` 按服务器系统时区计算。

### 迁移服务器

比如从"一台电脑"换到云服务器：

1. **先停掉旧服务器**：两个服务器同时收微信消息会互相抢。
2. 把旧服务器数据目录（Windows 上是 `%USERPROFILE%\.chatpilot\server\`）里的 `state.json` 放进安装包目录，和 `install-server.sh` 放在一起，再运行安装脚本。微信绑定和已配对的设备都会跟过来，不用重新扫码。旧服务器开着 TLS 的话，把 `server.pfx`（证书）也放进去，指纹就不会变。
3. 原来配对过的电脑**不用重新配对**（设备令牌还有效）：把节点配置 `~/.chatpilot/node.json` 里的 `serverUrl` 改成新地址；证书没迁移的话，`serverFingerprint` 改成新服务器打印的指纹。改完重启节点。
4. 确认一切正常后，删掉安装包目录里的 `state.json` 和 `server.pfx`：里面有微信的 bot token。

不用脚本的话，把这两个文件拷到新服务器的数据目录（Linux 上是 `/var/lib/chatpilot/`），属主 `chatpilot`、权限 600。

### 卸载

- 撤销某台电脑：微信里发 `/unpair 设备名`，那台电脑的节点会立即断开并退出，令牌作废。
- 删除节点：停掉自启服务，删除可执行文件和 `~/.chatpilot/node.json`、`~/.chatpilot/node.tasks.json`。
- 删除服务器：停掉服务，删除可执行文件和数据目录。数据目录里有微信的 bot token，请彻底删除。

## 使用指南

### 指令一览

| 指令 | 作用 |
|---|---|
| `/help` | 显示指令说明 |
| `/devices` | 设备列表和在线状态 |
| `/use 设备` | 切换默认设备 |
| `/ls [设备]` | 列出 agent（带序号） |
| `/pending` | 待办：谁在等确认、谁在干活、排了几条、定时任务 |
| `@目标 内容` | 把任务交给 agent，做完会推送它的回复；它在忙就排队 |
| `/re 内容` | 交给最近打过交道的 agent |
| `/read [目标] [行数]` | 看最近的终端输出 |
| `/last [目标]` | 补看 agent 最近一条回复（和做完时推送的一样） |
| `/diff [目标] [文件]` | 看 agent 工作目录里的 git 改动 |
| `/new [设备] 目录 [类型] [名字]` | 在允许的目录里新开工作区并启动 agent |
| `/yes [目标]` · `/no [目标]` | 同意 / 拒绝确认框 |
| `/keys 目标 按键…` | 发送白名单内的按键 |
| `/stop [目标]` | 打断 agent，清空排队的指令 |
| `/close 目标` | 关闭 agent（会先让你确认） |
| `/name 目标 新名字` | 给 agent 改名 |
| `/watch [目标 \| all [设备]]` · `/unwatch …` | 盯着 agent：在电脑上派的活做完也通知 |
| `/at 时间 内容` | 定时派活；`/at` 列出，`/at del 编号` 取消 |
| `/history [条数]` | 最近做完的任务和今天的统计 |
| `/butler [设备]` | 创建管家 agent |
| 直接发文字 | 交给当前设备的管家 |
| 发图片 | 暂存起来，下一条派活时一起交给 agent；`/drop` 丢掉 |
| `/wake 设备` | 让同一局域网里的其他电脑唤醒它 |
| `/pair [设备名]` · `/unpair 设备` | 配对 / 撤销设备 |

### 指定目标

**目标**用来指定是哪个 agent，有四种写法，都不区分大小写：

| 写法 | 例子 | 说明 |
|---|---|---|
| 序号 | `@2` | 最近一次 `/ls` 里的序号；`/ls` 之后 agent 有增减时，先重新 `/ls` |
| agent 名字 | `@tideq` | herdr 里给 agent 起的名字，用 `/name` 设置 |
| 工作区名 | `@SitePivot-Api` | herdr 工作区的名字；一个工作区里有多个 agent 时会提示你选 |
| pane ID | `@w2:pF` | herdr 的 pane 编号，`/ls` 里没名字的 agent 会显示它 |

前面加 `设备/` 指定设备，例如 `@mac/tideq`、`/read win/2`。不加时在所有在线设备里找：先按名字和 pane ID 精确匹配，找不到再按工作区名；多处匹配时优先默认设备，仍然分不清就列出来让你选，不会乱发。

**不写目标**：`/read`、`/last`、`/diff`、`/stop` 不写目标时，用"最近打过交道的 agent"：你最近指定过的（派活、看输出、`/new` 新开的……），或者你从聊天里派的活最近一次有了结果（做完、出错、等确认）的那个。`/re 内容` 也是发给它。只有你自己的操作会换掉它：`/watch` 盯着的 agent 自己干完活、`/at` 定时任务派的活，只通知你，不会悄悄把 `/stop`、`/re` 换成发给它们——要处理它们请写上目标，通知里有写法。

`/yes`、`/no` 不写目标时：最近打过交道的 agent 正在等确认就是它（比如派活时被告知"它正在等你确认"、`/new` 新开的卡在确认框）；不然看其他在等确认的 agent，只有一个就是它；有好几个、或者有候选所在的电脑离线或没响应时，列出来让你写目标，不会按任何键。`/close`、`/keys`、`/name` 必须写目标。

```text
⚠️ [mac] tideq 在等你确认：…
你：/yes
🤖 👌 [mac] tideq：已选择：1. Yes
✅ [mac] tideq 做完了：…
你：/diff
你：/re 改得不错，再补个测试
```

### 派活和通知

```text
@tideq 把 README 里的安装步骤更新一下，改完跑一遍文档检查
```

发出后立刻收到"📨 已发给…"。之后会根据 agent 的状态推送：

| 通知 | 什么时候 |
|---|---|
| ✅ `做完了` + 回复内容 | agent 做完、回到空闲 |
| ⚠️ `在等你确认` + 屏幕内容 | agent 卡在确认框上，后面附 `/yes`、`/no` 的写法 |
| ❌ `出错了` + 原因 | 提交后 10 秒没开始工作、等了太久（默认 6 小时）、读取失败等 |

- 回复超过 4000 字会拆成多条，最多 4 条，再长的部分截断并提示。
- 单条指令最长 16384 字。
- agent 刚启动时，界面没准备好可能吞掉输入。节点会检查：确定指令被吞了（agent 没反应、屏幕上也找不到这段文字）才自动重发一次；拿不准就只提醒你，绝不让同一条指令执行两次。
- agent 正在干活（不管是你从手机派的，还是在电脑上开的）时再派活，指令会**排队**，回复"已排队（第 N 条）"；它做完一条再发下一条，每条做完都通知你，通知里会说还有几条在排队。每个 agent 最多排 10 条，`/stop` 清空。排队的指令存在节点上，节点重启也不丢。
- 先发图片、再派活：图片会一起交给 agent（见[发图片](#发图片)）。
- 晚到的消息不执行：服务器停机、或者 Telegram 代理不通时，你发的消息会等通道恢复后才收到。晚了超过 10 分钟的不再执行（那时情况可能变了，你可能也已经换个通道重发过了），只回复你哪几条没执行，需要的话重新发。微信消息带着发送时间时同样处理。
- 节点重启（升级、电脑重启）不会丢掉正在等的任务：重启后接着等，它在节点停着的时候做完了的话，启动后马上通知。服务器重启也不影响：节点断线期间的通知会在重连后补发。

### 处理确认框

agent 想执行命令、改文件时，常常会弹出确认框，这时你会收到 ⚠️ 通知和屏幕内容。

- `/yes 目标`：自动找到"这一次同意"的选项并选中（例如 `1. Yes`、`Allow once`、`Run (once)`）。如果选项只剩"不再询问""总是允许""本会话 / 本项目都允许"这类会扩大权限的，不会自动选，会把选项列出来。
- `/no 目标`：按 Esc 拒绝。
- `/keys 目标 按键…`：自己按键，最多 20 个。允许的按键：`enter esc tab shift+tab up down left right backspace ctrl+c y n 1`–`9`。例如 `/keys api down down enter`。

`/yes`、`/no` 只在 agent 确实停在确认框上时才按键，避免迟到的 `/yes` 把回车按进输入框、迟到的 `/no` 用 Esc 打断正在干的活。

`/yes` 还只同意发给你看过的那个确认框。通知发出后，原来的确认框可能已经在电脑上处理掉、又弹出了新的；这时 `/yes` 不按键，而是把新的确认框发给你，看过没问题再回一次 `/yes`。同一个确认框也只同意一次，连发两次 `/yes` 不会把紧接着弹出的下一个也同意了。没收到过通知的确认框（比如在电脑上派的活）同样是第一次 `/yes` 先发给你看。各 agent 确认框的识别情况见 [`docs/AGENTS.md`](docs/AGENTS.md)。

### 新开 agent

```text
/new D:\code\web                     在默认设备上用默认类型新开
/new mac ~/code/api codex            指定设备和类型
/new mac ~/code/api claude apibot    再指定名字
/new “D:\代码\我的 项目” codex        路径有空格时用引号包住（中英文引号都行）
```

- 目录必须在节点配置的 `allowedRoots` 之内，`..` 之类的路径穿越会被拒绝。
- 类型可以是 herdr 支持的 24 种（claude、codex、pi、kimi、qwen、opencode……），或节点配置 `launchers` 里自定义的（默认带 `reclaude`）。不写时用 `defaultAgentKind`。
- 名字只能用小写字母开头，包含小写字母、数字、`_`、`-`，最长 32 位；不写时按目录名生成。
- 新目录第一次打开时，agent 常会先问"是否信任这个目录"，你会收到 ⚠️ 通知，回 `/yes` 即可。

### 查看终端

`/read 目标 [行数]` 返回最近的终端输出，默认 60 行，可以是 1–500 行。输入框、空行和界面装饰会被去掉。适合在通知不够详细、或者想看 agent 正在干什么时用。

`/last 目标` 返回 agent 最近一条回复，取法和做完时推送的一样（能读会话记录就是完整原文）。微信的通知不保证送达，没收到"做完了"的时候用它补看；agent 还在干活时，返回的是目前最新的一条。

### 看改动

`/diff [目标]` 返回 agent 工作目录的 git 情况：分支、没提交的改动（`M` 修改、`A` 新增、`D` 删除、`??` 未跟踪）、改动行数统计、最近 3 次提交。`/diff 目标 文件` 看一个文件的具体 diff（路径相对 agent 的工作目录，例如 `/diff tideq src/app.ts`）。只读，不会改动仓库。需要那台电脑装了 git；开机自启时找不到 git 的话，在节点配置的 `gitPath` 里写绝对路径。

### 打断和关闭

- `/stop [目标]`：清空排队的指令；agent 正在干活时按 Esc 打断它（Claude Code、Codex、pi 等都是 Esc 打断）。在等确认或空闲时不按键。
- `/close 目标`：关闭 agent 所在的终端，agent 进程随之结束。第一次只回复要关闭的是哪个（设备、类型、目录），2 分钟内回复 `/close 设备/目标 确认` 才真正关闭。这 2 分钟里单独回"确认""好的"或者不写目标的 `/yes`，不会关闭、也不会交给管家或回应别的确认框，只会再告诉你完整的写法。

### 盯着电脑上的活

ChatPilot 默认只通知通过它派的活。在电脑前开了个长任务、然后出门了？`/watch 目标` 让节点盯着它：做完或者卡在确认框时一样推送给你。

- `/watch all [设备]`：盯着这台（不写设备就是所有在线设备）电脑上的全部 agent。
- `/watch`：看现在在盯谁；`/ls` 里被盯着的 agent 后面有 👀。
- `/unwatch 目标`、`/unwatch all [设备]`：停止。
- 盯着的设置存在节点上，重启后继续。通过 ChatPilot 派的活不会因为被盯着而重复通知。

### 待办和记录

- `/pending`：一条指令看全局——在等你确认的（附 `/yes` 写法）、在干活的（干了多久、排了几条）、定时任务和暂存的图片。
- `/history [条数]`：最近做完或出错的任务（默认 10 条，最多 30 条），每条有时间、耗时、任务和结果的摘要，开头是今天做完几个、一共干了多久。记录存在服务器数据目录的 `history.json`，只保存摘要。

### 定时派活

```text
/at 23:00 @api 跑一遍全量测试          今天 23:00（已经过了就是明天）
/at 明天 9:00 整理一下昨天的提交         交给管家
/at 30m @2 看看测试跑完没有              30 分钟后
```

时间可以写 `23:00`、`明天 9:00`、`30m`、`2h`、`1h30m`、`45分钟`、`2小时`，最远 7 天。时刻按服务器配置的 `TimeZone` 计算（发布包里默认 `Asia/Shanghai`），回复里会写明具体执行时间。

发给谁在创建时就定下来，回复里写着，例如 `@mac/api 跑一遍全量测试`、`[mac] 的管家：整理一下昨天的提交`：`@目标`（序号、名字、工作区名）立即换成"设备/名字"，交给管家的就是当时默认设备的管家。之后换了默认设备、别的电脑上有了同名的 agent，都不影响它发去哪里；到点时那个 agent 不在了（关掉、改名）就不执行，告诉你原因。所以创建时目标要能找到（电脑在线），而且 agent 要有名字：没名字的先用 `/name` 起一个，pane 编号以后可能换给别的程序。

到点后把内容发给定好的 agent，结果推送给你；不会带上你暂存的图片，也不会改变不写目标时的"最近打过交道的 agent"。`/at` 列出所有定时任务，`/at del 编号` 取消，回复"已取消"的任务一定不会再执行。服务器停机错过执行时间超过 10 分钟的不再执行，只通知你。

### 发图片

在微信（或 Telegram）里先发一张或几张图片，ChatPilot 会回"🖼 收到图片"；接下来的一条 `@目标 内容`、`/re` 或者交给管家的文字会带上这些图片。节点把图片存到 agent 工作目录下的 `.chatpilot/attachments/`（里面带一个 `.gitignore`，不会进 git），再在你的指令后面附上图片路径，agent 自己去看。

- 支持 PNG、JPG、GIF、WebP、BMP，单张最大 10 MB，最多先存 4 张；10 分钟内不派活会自动丢弃，`/drop` 立即丢掉。
- 文件、视频暂不支持，发了会告诉你没处理；语音会自动转成文字。Telegram 里"按文件发送"的图片也能用。

### 唤醒电脑

`/wake 设备` 让和它在同一个局域网里的另一台在线电脑广播 Wake-on-LAN 唤醒包。需要：

- 那台电脑的节点升级到支持 `/wake` 的版本后上线过一次（上线时会报告网卡地址）；
- 同一局域网里有另一台电脑的节点在线；
- 被唤醒的电脑在 BIOS / 网卡设置里打开了 Wake-on-LAN，接着电源，最好用网线（大多数无线网卡唤不醒）；macOS 打开"唤醒以供网络访问"。

醒来、节点连上后用 `/devices` 查看。

### 管家

管家是一个特别的 agent，名字固定叫 `butler`，专门替你分派任务：

1. 发 `/butler [设备]`，ChatPilot 会在那台电脑上新开一个 agent，并告诉它：用 herdr 查看和指挥其他 agent；没有你明确要求时不关闭、删除任何东西；每次用简短的中文回复。
2. 之后你直接发文字（不带 `/` 或 `@`），就会交给当前设备的管家，例如"看看哪个 agent 闲着，让它把测试跑一遍"。

没有管家时直接发文字，会提示你创建一个或用 `@目标` 指定 agent。

### 管理设备

- `/devices`：所有设备和在线状态，🟢 在线、⚪ 离线。
- `/use 设备`：切换默认设备。`/new`、`/butler`、直接发的文字没写设备时用默认设备；没选过默认设备时，如果只有一台在线就用它。`/ls` 不写设备时列出所有设备。
- `/pair [设备名]`：生成配对码（8 位，10 分钟有效，只能用一次）。设备名可以用中英文、数字、`_`、`-`，最长 32 个字符。
- `/unpair 设备`：撤销设备，它的节点立即断开、令牌作废；要重新接入得重新配对。

电脑掉线（睡眠、断网、节点退出）超过 2 分钟没连回来，会收到 ⚠️ 离线通知；连回来时再收到 ✅ 恢复通知。几秒内就连回来的（网络抖动、节点升级重启）不通知。服务器重启期间掉线的电脑也会通知。用服务器配置的 `OfflineNoticeSeconds` 调整等待时间，设成 0 关闭。

## 支持的 agent

| | agent |
|---|---|
| 真机验证过 | Claude Code / reclaude、Codex、pi |
| 用真实会话文件核对过 | Kimi Code |
| 按官方发布包或源码实现 | Qwen Code、opencode、Kilo Code、Droid、Cursor Agent、Copilot CLI、Grok Build |
| 只读终端 | Gemini CLI，以及 herdr 支持的其他类型（agy、amp、cline、devin、hermes、kiro、letta、maki、mastracode、muse、omp、qodercli） |

所有类型都能派活、看终端、发按键、收到做完的通知；差别在推送的回复是不是完整原文，以及 `/yes` 能不能自动识别确认框。每种 agent 的会话记录位置、环境变量和验证情况见 [`docs/AGENTS.md`](docs/AGENTS.md)。

## 配置

### 服务器配置

`appsettings.json` 的 `ChatPilot` 节，也可以用环境变量覆盖：层级用两个下划线连接，例如 `ChatPilot__Port=9443`、`ChatPilot__Tls__Enabled=false`。

| 配置 | 默认 | 说明 |
|---|---|---|
| `ListenAddress` | `0.0.0.0` | 监听地址；不开 TLS 时只能是回环地址 |
| `Port` | `8443` | |
| `PublicUrl` | 无 | 节点连接用的地址，会写进 `/pair` 返回的命令 |
| `DataDir` | `~/.chatpilot/server` | 状态文件、证书、登录二维码（权限 600） |
| `Tls:Enabled` | `true` | 自签证书，首次启动自动生成 |
| `CommandTimeoutSeconds` | `30` | 普通指令等节点回复多久（5–300） |
| `StartAgentTimeoutSeconds` | `120` | `/new` 等节点回复多久（10–600） |
| `OfflineNoticeSeconds` | `120` | 电脑断开多少秒还没连回来就通知离线（0–86400，0 表示不通知） |
| `WeChat:Enabled` | `true` | 关掉后只能用测试通道 |
| `WeChat:MaxChunkChars` | `4000` | 单条微信消息最多多少字 |
| `WeChat:MaxChunks` | `4` | 一次回复最多拆成几条 |
| `DevChannel:Enabled` | `false` | 本机测试通道，见[开发](#开发)；只接受回环地址，必须设至少 16 位的 `DevChannel:Token` |
| `TimeZone` | 系统时区（发布包的 `appsettings.json` 里是 `Asia/Shanghai`） | 你所在的时区：`/at 23:00` 按它计算，回复里的时间按它显示。云服务器的系统时区通常是 UTC，记得设 |
| `WeChat:CdnBaseUrl` | `https://novac2c.cdn.weixin.qq.com/c2c` | 下载你发的图片用的微信 CDN |
| `Telegram:Enabled` | `false` | 启用 Telegram 备用通道，见下面 |
| `Telegram:BotToken` | 无 | @BotFather 给的 token；建议用环境变量 `ChatPilot__Telegram__BotToken` 配置 |
| `Telegram:OwnerUserId` | 无 | 你的 Telegram 用户 ID（数字），只有这个用户的私聊消息会被处理 |
| `Telegram:Proxy` | 无 | 访问 Telegram 的代理，例如 `http://127.0.0.1:7890`、`socks5://127.0.0.1:1080` |

**Telegram 备用通道**：iLink 不是公开接口，腾讯调整时微信可能暂时用不了。可以再接一个 Telegram 机器人，指令、回复、通知和微信完全一样（通知两边都发，各发各的：一边连不上不会耽误另一边）：

1. 在 Telegram 里找 @BotFather 创建机器人，拿到 token；
2. 给你的机器人随便发一条消息，然后看服务器日志，会有一行"忽略一条 Telegram 消息：来自用户 123456789"，这个数字就是你的用户 ID；
3. 配置 `Telegram:Enabled=true`、`Telegram:BotToken`、`Telegram:OwnerUserId`，重启服务器。服务器在国内的话还要配 `Telegram:Proxy`。

在 Telegram 里发图片时可以直接把指令写在图片说明里，例如图片配文 `@tideq 看看这个报错`。一次发几张（相册）时，几张都收下了才执行说明里的指令；有图片没收到、或者附的是还不支持的文件时，说明里的指令不执行，会告诉你，补发之后再发一次指令。

### 节点配置

`~/.chatpilot/node.json`（权限 600），`pair` 时生成，改完重启节点生效。也可以用 `--config 路径` 或环境变量 `CHATPILOT_NODE_CONFIG` 指定别的位置。

| 字段 | 默认 | 说明 |
|---|---|---|
| `allowedRoots` | 配对时的 `--root` | `/new` 允许打开的目录（含子目录） |
| `defaultAgentKind` | `reclaude` 或 `claude` | `/new` 不写类型时用哪种；配对时装了 reclaude 就默认它 |
| `launchers` | 带 `reclaude` | 自定义启动命令，见下面的例子 |
| `herdrPath` | `herdr` | herdr 可执行文件；开机自启时 PATH 可能很短，建议写绝对路径 |
| `herdrSession` | 无 | 用 herdr 的命名会话；不写用默认会话 |
| `claudeProjectRoots` | 无 | 额外的 Claude 会话记录目录（`…/projects`） |
| `commandTimeoutSeconds` | `25` | 单次 herdr 调用的超时（5–25） |
| `agentStartTimeoutSeconds` | `60` | 等 agent 启动就绪多久（10–90） |
| `promptWaitHours` | `6` | 一个任务最多等多久才放弃通知（1–48） |
| `maxReplyChars` | `12000` | 推送回复的最大字数（500–16000），超出时保留结尾 |
| `gitPath` | `git` | `/diff` 用的 git；开机自启时 PATH 可能很短，找不到时写绝对路径 |

节点配置旁边还有几个它自己维护的文件（同样是 600）：`node.tasks.json` 正在跟踪的任务，`node.queue.json` 排队的指令，`node.watch.json` 在盯哪些 agent。

`launchers` 用来接入 herdr 不能直接启动的命令，例如包装过的启动脚本。节点会在新终端里运行 `command`，等 herdr 识别出 `detectAs` 类型的 agent：

```json
"launchers": {
  "reclaude": { "command": "reclaude", "detectAs": "claude" },
  "work-claude": { "command": "claude --model opus", "detectAs": "claude" }
}
```

`command` 只来自本机配置，手机发不了任意命令；`detectAs` 必须是 herdr 认识的类型。

## 安全

ChatPilot 能让手机远程给 agent 派活，而 agent 能读写代码、执行命令，所以请认真对待：

- 只有绑定的那个微信号能发指令，其他人发的消息直接忽略。
- 节点用 256 位随机令牌加证书指纹连服务器；服务器只存令牌的哈希。配对码一次性、10 分钟有效；配对和令牌错误都按 IP 限流。
- 节点是安全边界：只执行协议里定义的几种 herdr 操作，`/new` 只能打开白名单目录，`/keys` 只能发白名单按键，所有参数都校验，不经过 shell。`/diff` 只运行只读的 git 命令，并关掉了外部 diff、文本转换、fsmonitor（仓库自己配置的 clean 过滤器 git 没法关，和你自己运行 `git status` 一样会执行）；`/close` 要你确认；`/wake` 只广播唤醒包。
- 图片只在服务器内存里暂存 10 分钟，节点要带自己的令牌才能下载发给它的图片；节点只保存真的图片，文件名自己决定。
- 启用 Telegram 时，只有配置的那个 Telegram 用户在私聊里发的消息会被处理。
- **服务器被攻破，就等于攻击者能给你所有电脑上的 agent 派活。** 请用自己的服务器、及时更新系统、只开放 8443 和 SSH，SSH 只允许密钥登录。
- 你的指令和 agent 的回复会经过服务器和腾讯；不要让 agent 把密钥之类的内容发到聊天里。
- 怀疑某台电脑的令牌泄露时，`/unpair` 之后重新配对。

完整的威胁模型和控制措施见 [`docs/SECURITY.md`](docs/SECURITY.md)。发现安全问题请在 GitHub 仓库的 Security 页面点 "Report a vulnerability" 私下报告，不要公开提 Issue。

## 排错

**先看日志和自检**：

| | 日志 | 自检 |
|---|---|---|
| 服务器（Linux） | `journalctl -u chatpilot-server -f` | `curl -k https://127.0.0.1:8443/healthz` |
| 服务器（Windows 计划任务） | `%LOCALAPPDATA%\ChatPilot\server.log` | |
| 节点（Windows） | `%LOCALAPPDATA%\ChatPilot\node.log` | `chatpilot-node doctor` |
| 节点（macOS） | `~/Library/Logs/chatpilot-node.log` | `chatpilot-node doctor` |
| 节点（Linux） | `journalctl --user -u chatpilot-node -f` | `chatpilot-node doctor` |

日志里不会写令牌、配对码、消息内容和微信登录链接。

**常见情况**：

| 现象 | 原因和处理 |
|---|---|
| 微信里发消息没反应 | 看服务器日志：微信是否绑定成功；bot token 失效时会暂停 1 小时重试，一直失效就重新 `login` |
| 绑定时要输入验证码，服务没反应 | 后台运行时没法输入。停掉服务，在终端里登录：Linux 上是 `sudo -u chatpilot env ChatPilot__DataDir=/var/lib/chatpilot /opt/chatpilot/chatpilot-server login`，Windows 上直接运行 `chatpilot-server.exe login` |
| `/devices` 显示设备离线 | 看节点日志：服务器地址、证书指纹对不对，网络能不能到 8443；节点断线后会按 1、2、4…60 秒退避自动重连 |
| 节点启动后立刻退出、退出码 2 | 设备已被 `/unpair` 或令牌无效，重新配对 |
| `/new` 报 "timed out waiting for agent startup" | 先在那台电脑的终端里手动运行一次这个 agent，确认能启动、已登录；Windows 上检查 PowerShell 执行策略（见[快速开始](#快速开始)） |
| `/new` 报"不在允许的目录内" | 目录不在节点的 `allowedRoots` 里，改配置后重启节点 |
| 推送的回复不完整，或者混着工具输出 | 给这个 agent 装上 herdr 集成，节点就能直接读会话记录；见 [`docs/AGENTS.md`](docs/AGENTS.md) |
| "提交后 10 秒内没看到它开始工作" | agent 可能还在启动或卡住了，用 `/read` 看看；确定被吞的指令节点已经自动重发过 |
| `/yes` 说"没在等确认" | agent 已经不在确认框上了，用 `/read` 确认；确认框还在的话用 `/keys` 手动按 |
| `/yes` 说"还没发给你看过"或"不是同一个了" | 没按任何键，回复里附的是现在的确认框；看过没问题再回一次 `/yes` |
| 长时间没说话后收不到通知 | 微信的限制：通知复用你最近一条消息的上下文，随便发一条消息就能恢复；漏掉的回复用 `/last 目标` 补看 |

## 常见问题

**要买证书或域名吗？** 不用。服务器自动生成自签证书，节点按指纹校验；手机经腾讯中转，不直接连服务器。

**每台电脑都要装服务器吗？** 不用，也不要。服务器只要一份，一个微信机器人对应一个服务器；其他电脑只装节点、配对到这个服务器。

**能几个人一起用吗？** 不能。一个服务器只认绑定的那一个微信号，其他人发的消息会被忽略。

**要花钱吗？** ChatPilot 本身免费、开源，也不调用任何收费接口；花的只是你本来就在用的 agent 的额度，以及云服务器的钱（如果用的话）。

**电脑睡眠或关机了会怎样？** 节点离线，`/devices` 显示 ⚪，发给它的指令会提示离线。macOS 的自启配置自带 `caffeinate` 防止空闲睡眠。

**节点重启时正在跑的任务会怎样？** 任务本身不受影响（它在 herdr 里跑），只是这次做完不会再推送通知，用 `/ls`、`/read` 查看结果。

**不用微信能用吗？** 可以用 Telegram（见[配置](#服务器配置)里的 Telegram 备用通道），也可以两个一起用。服务器还有一个本机测试通道（HTTP 接口），主要用于开发调试，见[开发](#开发)。

**为什么一定要 herdr？** 见[准备工作](#准备工作)。

## 已知限制

- **iLink 不是腾讯公开文档的接口**：协议参照腾讯开源的 [openclaw-weixin](https://www.npmjs.com/package/@tencent-weixin/openclaw-weixin) 插件，腾讯修改或限制时需要跟着调整。
- **微信不支持真正的主动推送**：通知复用你最近一条消息的 context_token，长时间不说话后能否推送取决于腾讯。
- **会话记录是各 agent 的内部格式**：agent 升级后可能变化，读不了时自动退回读终端。
- 节点重启后接着跟踪的只有派活和 `/yes` 之后的任务（排队的指令、盯梢设置也会保留）；管家的初始说明还没发出去时节点重启，需要重新 `/butler`。电脑重启后 herdr 里的终端都是新的，之前的任务、排队的指令不再跟踪（会通知你没发出去）。
- 盯梢靠每 5 秒查一次 agent 状态：几秒内就做完的小任务可能漏报。
- 图片下载按腾讯开源插件的做法实现，iLink 调整时可能需要跟着改；只支持图片，不支持文件和视频。
- `/wake` 依赖电脑和网络本身支持 Wake-on-LAN，无线网卡、跨网段通常唤不醒。
- 同一个设备只能有一个节点在线：新启动的会把旧的顶掉，被顶掉的那个自动退出。
- 消息按顺序处理，一条很慢的指令（比如 `/new`）会让后面的消息稍等。
- 不建议放在反向代理后面：所有请求的来源 IP 都会变成代理，限流就成了所有人共享。原因见 [`docs/SECURITY.md`](docs/SECURITY.md)。

## 开发

```bash
dotnet build ChatPilot.sln     # 警告视为错误；src 下公开成员必须有 XML 文档注释
dotnet test ChatPilot.sln      # 单元测试 + 集成测试 + 安全测试（模拟 herdr、模拟 iLink、内存服务器）
```

每个 PR 都会由 GitHub Actions 在 Linux、macOS、Windows 上各跑一遍测试，三项都通过才能合并。

### 自己打包

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)；Windows 上在 Git Bash 里运行。

```bash
RIDS="win-x64" scripts/publish.sh        # 输出到 artifacts/win-x64/{server,node}/
```

可选平台：`win-x64`、`osx-arm64`、`osx-x64`、`linux-x64`、`linux-arm64`，不写 `RIDS` 就全部打包。输出目录和 Releases 里的压缩包内容一样，程序旁边带着安装和开机自启脚本。macOS 的程序要在 Mac 上打包，否则没有签名，在 Apple 芯片上运行不了。

### 发布新版本

1. 通过 PR 把 [`Directory.Build.props`](Directory.Build.props) 里的 `Version` 改成新版本号，合并。
2. 在 main 上打标签并推送：`git tag v0.2.0 && git push origin v0.2.0`。
3. GitHub Actions 会编译 5 个平台、打包，连同 `SHA256SUMS.txt` 发布到 Releases。标签带 `-`（例如 `v0.2.0-beta.1`）时发布为预发布版本。

不用微信也能在本机调试：服务器开测试通道，用 HTTP 模拟手机发消息，`GET /dev/events` 取主动通知。

```bash
ASPNETCORE_ENVIRONMENT=Development ChatPilot__WeChat__Enabled=false \
ChatPilot__DevChannel__Enabled=true ChatPilot__DevChannel__Token=至少16位的随机字符串 \
  dotnet run --project src/ChatPilot.Server
curl -s localhost:8080/dev/chat -H 'X-ChatPilot-Dev-Token: 同上' -H 'Content-Type: application/json' -d '{"text":"/pair mac"}'
dotnet run --project src/ChatPilot.Node -- pair --server http://127.0.0.1:8080 --code 上一步的配对码
dotnet run --project src/ChatPilot.Node -- run
curl -s localhost:8080/dev/events -H 'X-ChatPilot-Dev-Token: 同上'
```

想在不影响日常 agent 的情况下实测，可以让节点用 herdr 的命名会话（配对时加 `--session 名字`），测试用的 agent 都开在那个会话里。

```text
src/ChatPilot.Shared   协议、校验、证书指纹、安全文件写入
src/ChatPilot.Server   服务器：微信接入、设备连接、指令解析与分发
src/ChatPilot.Node     节点：调用 herdr、目录白名单、会话记录和终端解析、确认框导航、连接与重连
tests/ChatPilot.Tests  测试
deploy/                开机自启样例（systemd、launchd、Windows 计划任务）
docs/                  设计、验收、安全、各 agent 支持细节
```

设计和验收记录在 [`docs/PLAN.md`](docs/PLAN.md)、[`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md)。

## 贡献

欢迎提 Issue 和 Pull Request。`main` 分支受保护，所有改动（包括维护者自己的）都通过 Pull Request 合并：先 fork，在自己的分支上改，跑通 `dotnet build` 和 `dotnet test`，再提 PR；三个系统上的自动测试都通过才能合并。详细约定见 [`CONTRIBUTING.md`](CONTRIBUTING.md)。

## 致谢

- [herdr](https://herdr.dev)：终端里的 agent 管理器，ChatPilot 的节点建立在它之上。
- 腾讯开源的 [openclaw-weixin](https://www.npmjs.com/package/@tencent-weixin/openclaw-weixin)（MIT）：iLink 微信协议的参考实现。

## 许可证

[MIT](LICENSE)
