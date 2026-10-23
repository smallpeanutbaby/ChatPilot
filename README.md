# ChatPilot

**用一部手机，通过微信指挥你所有电脑上的 AI 编码 agent。**

*Command the AI coding agents (Claude Code, Codex, …) on all your computers from WeChat on your phone.*

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

## 特点

- **一个微信管所有电脑**：每台电脑装一个节点，在同一个对话里用 `@mac/web`、`@win/api` 指定是哪台的哪个 agent。
- **没有额外的 AI**：理解指令、写代码的都是你电脑上原来的 agent；ChatPilot 只负责传话，不调用任何大模型接口。
- **支持 20 多种 agent**：Claude Code、Codex、pi、Kimi、Qwen、opencode、Cursor、Copilot……herdr 能管的都能用（[支持细节](docs/AGENTS.md)）。
- **结果自动推回来**：任务做完推送 agent 的回复，卡在确认框时推送屏幕内容，`/yes` 只会选"这一次同意"，不会替你选"以后都允许"。
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
| **服务器** `chatpilot-server` | 绑定微信、收你的消息、转发给对应电脑、把结果推回微信 | 只要一份：云服务器，或者只有一台电脑时直接放在这台电脑上 |
| **节点** `chatpilot-node` | 执行服务器发来的指令：列出 agent、派活、读终端、按确认键 | 每台要被控制的电脑一份 |
| **[herdr](https://herdr.dev)** | 终端里的 agent 管理器：开终端、识别 agent 在工作还是在等确认、读写终端 | 每台要被控制的电脑 |

扫码绑定微信是服务器做的，只做一次；以后加电脑只装节点、配对，不用再扫码。

## 准备工作

- **每台要被控制的电脑**：装好 [herdr](https://herdr.dev) 并打开过一次，以及你要用的 agent（Claude Code、Codex……）。为了让推送的回复完整，给常用的 agent 装上 herdr 集成，例如 `herdr integration install claude`、`herdr integration install codex`。
- **一个微信号**：用来绑定机器人、接收通知。
- **放服务器的地方**：一台云服务器（1 核 1G 就够，香港或海外不用备案）；只有一台电脑时也可以直接放在这台电脑上。不需要域名，也不需要买证书。
- **打包用**：[.NET 10 SDK](https://dotnet.microsoft.com/download)，只在打包的电脑上需要。

> **为什么一定要 herdr？** 节点不直接操作终端，而是通过 herdr：开终端、判断 agent 是在工作还是在等确认、读写终端、拿到会话记录，这些对 20 多种 agent 都要单独适配，herdr 已经做好了。不装 herdr 的话，节点什么也做不了。

## 快速开始：一台电脑先跑起来

服务器和节点放在同一台电脑上，不用云服务器。下面以 Windows 为例，macOS / Linux 同理。

**1. 打包**（Windows 上在 Git Bash 里运行）

```bash
RIDS="win-x64" scripts/publish.sh        # 输出到 artifacts/win-x64/{server,node}/
```

可选平台：`win-x64`、`osx-arm64`、`osx-x64`、`linux-x64`、`linux-arm64`，不写 `RIDS` 就全部打包。

**2. 配置服务器只听本机**：把 `artifacts/win-x64/server/` 拷到比如 `C:\Tools\chatpilot\server\`，修改里面的 `appsettings.json`：

```json
"ChatPilot": {
  "ListenAddress": "127.0.0.1",
  "PublicUrl": "http://127.0.0.1:8443",
  "Tls": { "Enabled": false }
}
```

只听本机时可以不开 TLS；服务器要对外时必须开（默认就是开的）。

**3. 绑定微信**

```powershell
C:\Tools\chatpilot\server\chatpilot-server.exe login
```

终端里会显示二维码，用手机微信扫码并确认，看到"✅ 微信绑定成功"就好了。之后微信里会出现和这个机器人的对话，以后都在这个对话里发指令。

**4. 让服务器常驻**（登录 Windows 后自动启动）

```powershell
powershell -ExecutionPolicy Bypass -File deploy\windows\install-server-task.ps1 -ServerPath C:\Tools\chatpilot\server\chatpilot-server.exe
```

**5. 配对节点**：在微信里发 `/pair win`，把返回的命令拿到这台电脑上运行，加上允许 `/new` 打开的目录：

```powershell
C:\Tools\chatpilot\node\chatpilot-node.exe pair --server http://127.0.0.1:8443 --code ABCD-EFGH --name win --root D:\code
C:\Tools\chatpilot\node\chatpilot-node.exe doctor
powershell -ExecutionPolicy Bypass -File deploy\windows\install-node-task.ps1 -NodePath C:\Tools\chatpilot\node\chatpilot-node.exe
```

**6. 试一下**：微信里发 `/devices`，看到 `🟢 win` 就通了；发 `/ls` 列出 agent，`@序号 内容` 派活。

> Windows 上用 npm 装的 codex、kimi、pi 是通过 `.ps1` 脚本启动的，如果 `/new` 启动它们时超时，执行一次 `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`。

## 正式部署：云服务器 + 多台电脑

### 服务器（Linux）

```bash
# 在打包的电脑上
RIDS="linux-x64" scripts/publish.sh
scp artifacts/linux-x64/server/{chatpilot-server,appsettings.json} 你的服务器:/tmp/

# 在服务器上（详细步骤见 deploy/linux/chatpilot-server.service 开头的注释）
sudo useradd --system --home /var/lib/chatpilot --shell /usr/sbin/nologin chatpilot
sudo mkdir -p /opt/chatpilot /var/lib/chatpilot && sudo chown chatpilot: /var/lib/chatpilot && sudo chmod 700 /var/lib/chatpilot
sudo mv /tmp/chatpilot-server /tmp/appsettings.json /opt/chatpilot/ && sudo chmod +x /opt/chatpilot/chatpilot-server
sudo -u chatpilot env ChatPilot__DataDir=/var/lib/chatpilot /opt/chatpilot/chatpilot-server login   # 扫码绑定微信
# 改好 service 文件里的 ChatPilot__PublicUrl（https://服务器IP:8443）再注册
sudo cp chatpilot-server.service /etc/systemd/system/ && sudo systemctl daemon-reload && sudo systemctl enable --now chatpilot-server
journalctl -u chatpilot-server -f    # 记下日志里的证书指纹 sha256:…
```

- 安全组 / 防火墙只放行 **8443/tcp**（和你的 SSH 端口）。
- **证书不用买**：第一次启动时自动生成自签证书（10 年有效）。节点配对时记下它的指纹，以后只认这张证书，比普通的 CA 校验更严。
- **从"一台电脑"迁移过来**：把原来的数据目录（Windows 上是 `%USERPROFILE%\.chatpilot\server\`）里的 `state.json` 拷到 `/var/lib/chatpilot/`（属主 chatpilot、权限 600），微信绑定和已配对的设备都会跟过来，不用重新扫码。证书会重新生成，所以节点要带上新指纹重新配对一次。

### 每台电脑：装节点

1. 微信里发 `/pair 设备名`，复制返回的命令在那台电脑上运行，命令里已经带好了 `--fingerprint`：

   ```bash
   chatpilot-node pair --server https://服务器IP:8443 --code ABCD-EFGH \
     --fingerprint sha256:… --name mac --root ~/code
   ```

   `--root` 是允许 `/new` 打开的目录，可以写多个；不写默认是用户主目录。
2. `chatpilot-node doctor` 自检：herdr、允许的目录、agent 类型、服务器连通性。
3. 设成开机自启：
   - **Windows**：[`deploy/windows/install-node-task.ps1`](deploy/windows/install-node-task.ps1)（登录后启动；不要注册成 Windows 服务，服务账户访问不到你的 herdr）
   - **macOS**：[`deploy/macos/com.chatpilot.node.plist`](deploy/macos/com.chatpilot.node.plist)（自带 `caffeinate`，防止电脑睡眠）
   - **Linux**：[`deploy/linux/chatpilot-node.service`](deploy/linux/chatpilot-node.service)

## 手机上怎么用

| 指令 | 作用 |
|---|---|
| `/devices` | 设备列表和在线状态 |
| `/use 设备` | 切换默认设备 |
| `/ls [设备]` | 列出 agent（带序号） |
| `@目标 内容` | 把任务交给 agent，做完会推送它的回复 |
| `/read 目标 [行数]` | 看最近的终端输出 |
| `/new [设备] 目录 [类型] [名字]` | 在允许的目录里新开工作区并启动 agent |
| `/yes 目标` · `/no 目标` | 处理确认框：只选"这一次同意"；会扩大权限的选项列出来让你用 `/keys` 自己选 |
| `/keys 目标 按键…` | 发送白名单内的按键，例如 `/keys api down enter` |
| `/name 目标 新名字` | 给 agent 改名 |
| `/butler [设备]` | 创建管家 agent：之后直接发文字（不带指令）就交给它，由它通过 herdr 分派 |
| `/pair [设备名]` · `/unpair 设备` | 配对 / 撤销设备 |

**目标**可以是 `/ls` 里的序号、agent 名字、工作区名或 pane ID，前面加 `设备/` 指定设备，例如 `@mac/web 跑一下测试`。

## 配置

**服务器**：`appsettings.json` 的 `ChatPilot` 节，也可以用环境变量覆盖（例如 `ChatPilot__Port=9443`）。

| 配置 | 默认 | 说明 |
|---|---|---|
| `ListenAddress` | `0.0.0.0` | 不开 TLS 时只能是回环地址 |
| `Port` | `8443` | |
| `PublicUrl` | 无 | 节点连接用的地址，会写进 `/pair` 返回的命令 |
| `DataDir` | `~/.chatpilot/server` | 状态文件、证书、登录二维码（权限 600） |
| `Tls:Enabled` | `true` | 自签证书，首次启动自动生成 |
| `WeChat:Enabled` | `true` | |

**节点**：`~/.chatpilot/node.json`（权限 600，`pair` 时生成）。

| 字段 | 说明 |
|---|---|
| `allowedRoots` | `/new` 允许的目录 |
| `defaultAgentKind` | `/new` 默认类型；装了 reclaude 时默认 reclaude，否则 claude |
| `launchers` | 自定义启动命令，默认带 `reclaude`（运行后等 herdr 识别出 claude） |
| `herdrSession` | 用 herdr 的命名会话（默认用默认会话） |
| `claudeProjectRoots` | 额外的 Claude 会话记录目录 |
| `promptWaitHours` | 一个任务最多等多久才放弃通知（默认 6 小时） |

## 安全

ChatPilot 能让手机远程给 agent 派活，而 agent 能读写代码、执行命令，所以请认真对待：

- 只有绑定的那个微信号能发指令；节点用 256 位令牌加证书指纹连服务器，配对码一次性、10 分钟有效。
- 节点是安全边界：只执行协议里定义的几种 herdr 操作，`/new` 只能打开白名单目录，`/keys` 只能发白名单按键，参数全部校验。
- **服务器被攻破，就等于攻击者能给你所有电脑上的 agent 派活**。请用自己的服务器、及时更新系统、只开放 8443 和 SSH，SSH 只允许密钥登录。
- 你的指令和 agent 的回复会经过服务器和腾讯。

完整的威胁模型和控制措施见 [`docs/SECURITY.md`](docs/SECURITY.md)。

## 常见问题

**要买证书或域名吗？** 不用。服务器自动生成自签证书，节点按指纹校验；手机经腾讯中转，不直接连服务器。

**每台电脑都要装服务器吗？** 不用，也不要。服务器只要一份，一个微信 bot 对应一个服务器；其他电脑只装节点、配对到这个服务器。

**推送的回复不完整，或者混着工具输出？** 给这个 agent 装上 herdr 集成（`herdr integration install <类型>`），节点就能直接读会话记录。各 agent 的情况见 [`docs/AGENTS.md`](docs/AGENTS.md)。

**`/new` 启动 agent 超时？** 先在那台电脑的终端里手动运行一次这个 agent，确认能启动、已登录；Windows 上检查 PowerShell 执行策略（见上文）。

## 已知限制

- **iLink 不是腾讯公开文档的接口**：协议参照腾讯开源的 [openclaw-weixin](https://www.npmjs.com/package/@tencent-weixin/openclaw-weixin) 插件，腾讯修改或限制时需要跟着调整。
- **微信不支持真正的主动推送**：通知复用你最近一条消息的 context_token，长时间不说话后能否推送取决于腾讯。
- 节点或服务器重启时，正在等的任务不再推送结果，用 `/ls`、`/read` 查看。
- 同一个设备只能有一个节点在线：新启动的会把旧的顶掉。
- 消息按顺序处理，一条很慢的指令（比如 `/new`）会让后面的消息稍等。
- 不建议放在反向代理后面（限流会变成所有人共享），原因见 [`docs/SECURITY.md`](docs/SECURITY.md)。

## 开发

```bash
dotnet build ChatPilot.sln     # 警告视为错误；src 下公开成员必须有 XML 文档注释
dotnet test ChatPilot.sln      # 单元测试 + 集成测试 + 安全测试（模拟 herdr、模拟 iLink、内存服务器）
```

不用微信也能在本机调试：服务器开测试通道，用 HTTP 模拟手机发消息。

```bash
ASPNETCORE_ENVIRONMENT=Development ChatPilot__WeChat__Enabled=false \
ChatPilot__DevChannel__Enabled=true ChatPilot__DevChannel__Token=至少16位的随机字符串 \
  dotnet run --project src/ChatPilot.Server
curl -s localhost:8080/dev/chat -H 'X-ChatPilot-Dev-Token: 同上' -H 'Content-Type: application/json' -d '{"text":"/pair mac"}'
dotnet run --project src/ChatPilot.Node -- pair --server http://127.0.0.1:8080 --code 上一步的配对码
dotnet run --project src/ChatPilot.Node -- run
```

```text
src/ChatPilot.Shared   协议、校验、证书指纹、安全文件写入
src/ChatPilot.Server   服务器：微信接入、设备连接、指令解析与分发
src/ChatPilot.Node     节点：调用 herdr、目录白名单、回复提取、连接与重连
tests/ChatPilot.Tests  测试
deploy/                开机自启样例（systemd、launchd、Windows 计划任务）
docs/                  设计、验收、安全、各 agent 支持细节
```

欢迎贡献，请先看 [`CONTRIBUTING.md`](CONTRIBUTING.md)。设计和验收记录在 [`docs/PLAN.md`](docs/PLAN.md)、[`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md)。

## 致谢

- [herdr](https://herdr.dev)：终端里的 agent 管理器，ChatPilot 的节点建立在它之上。
- 腾讯开源的 [openclaw-weixin](https://www.npmjs.com/package/@tencent-weixin/openclaw-weixin)（MIT）：iLink 微信协议的参考实现。

## 许可证

[MIT](LICENSE)
