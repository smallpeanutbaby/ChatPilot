# ChatPilot

用一部手机（微信 ClawBot）指挥多台电脑上的 AI 编码 agent（Claude Code、reclaude、Codex……）。
电脑上的 agent 由 [herdr](https://herdr.dev) 管理，ChatPilot 负责把手机上的话送到对应电脑的 agent，再把结果推回手机。

```
手机微信 ──► 腾讯 iLink ◄── ChatPilot 服务器（云上）
                                 ▲  WebSocket（TLS + 证书指纹校验）
                     ┌───────────┴───────────┐
               ChatPilot 节点（Mac）    ChatPilot 节点（Windows）…
                     │                       │
                   herdr                   herdr
                     │                       │
                Claude Code …           Claude Code …
```

- **两个程序**：服务器（只部署一份）和节点（每台电脑一份）。电脑只往外连服务器，不开端口、不需要公网 IP。
- **没有额外的 AI**：理解指令、写代码的都是你电脑上的 agent（Claude Code、Codex……）；服务器只转发。
- **herdr 能管的 agent 都能用**：claude、reclaude、codex、pi、kimi、qwen、opencode 等，各自的支持程度见[支持的 agent](#支持的-agent)。配对时检测到 reclaude 会把它设为默认，`/new` 时也可以指定类型。
- 设计和验收：[`docs/PLAN.md`](docs/PLAN.md) · [`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md) · [`docs/ACCEPTANCE-REPORT.md`](docs/ACCEPTANCE-REPORT.md) · 安全：[`docs/SECURITY.md`](docs/SECURITY.md)

## 手机上怎么用

| 指令 | 作用 |
|---|---|
| `/devices` | 设备列表和在线状态 |
| `/use 设备` | 切换默认设备 |
| `/ls [设备]` | 列出 agent（带序号） |
| `@目标 内容` | 把任务交给 agent，做完会推送它的回复 |
| `/read 目标 [行数]` | 看最近的终端输出 |
| `/new [设备] 目录 [类型] [名字]` | 在允许的目录里新开工作区并启动 agent |
| `/yes 目标` · `/no 目标` | 处理确认框：自动选中"这一次同意"的选项；"不再询问 / 全部允许"这类会扩大权限的选项不会自动选，会列出来让你用 `/keys` 自己选 |
| `/keys 目标 按键…` | 发送白名单内的按键，例如 `/keys 1 down enter` |
| `/name 目标 新名字` | 给 agent 改名 |
| `/butler [设备]` | 创建管家 agent |
| 直接发文字 | 交给当前设备的管家，由它通过 herdr 分派 |
| `/pair [设备名]` · `/unpair 设备` | 配对 / 撤销设备 |

**目标**可以是 `/ls` 里的序号、agent 名字、工作区名或 pane ID，前面加 `设备/` 指定设备，例如 `@mac/tideq 跑一下测试`。

## 支持的 agent

`/new` 能启动 herdr 0.9.4 支持的全部类型（agy、amp、claude、cline、codex、copilot、cursor、devin、droid、gemini、grok、hermes、kilo、kimi、kiro、letta、maki、mastracode、muse、omp、opencode、pi、qodercli、qwen），再加上 `launchers` 里自定义的（默认带 reclaude）。
发任务、`/read`、`/keys`、改名、做完通知都直接走 herdr，不分类型。各类型的差别在两处：

- **推送的回复**：能找到会话记录就读记录，内容完整准确；找不到就读终端。读终端时，Claude Code 和 Codex 能认出回复块，其他 agent 取这一轮指令之后的全部输出（可能夹着工具调用的输出）。
- **`/yes`**：认得竖排选项列表里的选中标记（`❯ › → ▶ ▸ > ●` 和单选框 `(●)`），只选"这一次同意"。横排按钮（opencode / kilo 的 Allow once · Allow always · Reject）看不出选中的是哪个，不自动选，请用 `/keys`。

| 类型 | 回复从哪里读 | 验证情况 |
|---|---|---|
| claude / reclaude | 会话记录：`~/.claude/projects`、`CLAUDE_CONFIG_DIR`、reclaude 各账号目录、配置的 `claudeProjectRoots` | macOS 真机验收；Windows 真机冒烟测试通过（`/new`、信任目录的 `/yes`、推送回复） |
| codex | 会话记录：`$CODEX_HOME`（默认 `~/.codex`）下的 `sessions`、`archived_sessions` | Windows 真机冒烟测试通过（`/new`、推送回复）；`/yes` 只有测试样例，未在真机触发确认框 |
| pi | 会话记录：herdr 直接给出路径 | Windows 真机冒烟测试通过；pi 默认没有确认框 |
| kimi | 会话记录：`$KIMI_CODE_HOME`（默认 `~/.kimi-code`）下的 `sessions` | 本机真实会话核对；真机测试因套餐无权限未完成 |
| qwen | 会话记录：`$QWEN_RUNTIME_DIR` / `$QWEN_HOME`（默认 `~/.qwen`）下的 `projects/*/chats` | 按 Qwen Code 0.26 发布包实现，未真机验证 |
| opencode / kilo | SQLite 库：`$XDG_DATA_HOME`（默认 `~/.local/share`）下的 `opencode/opencode.db`、`kilo/kilo.db`，可用 `OPENCODE_DB` / `KILO_DB` 指定 | 按 opencode 1.19 / kilo 7.8 源码实现，未真机验证 |
| droid | 会话记录：`$FACTORY_HOME_OVERRIDE`（默认用户主目录）下的 `.factory/sessions` | 按 Droid 0.248 发布包实现，未真机验证 |
| cursor | 会话记录：`$CURSOR_DATA_DIR`（默认 `~/.cursor`）下的 `projects/*/agent-transcripts`；Cursor 会把思考过程接在回复正文后面，无法拆开 | 按 Cursor Agent CLI 发布包实现，未真机验证 |
| copilot | 会话记录：`$COPILOT_HOME`（默认 `~/.copilot`）下的 `session-state` | 按 Copilot CLI 1.0.63 发布包实现，未真机验证 |
| grok | 会话记录：`$GROK_HOME`（默认 `~/.grok`）下的 `sessions/*/<id>/updates.jsonl`；有用户报告 Windows 上不写这个文件，那时退回读终端 | 按官方文档实现，可信度中等，未真机验证 |
| gemini | 终端（herdr 不上报它的会话） | 未真机验证 |
| 其他类型 | 终端 | 未真机验证 |

读会话记录需要 herdr 上报会话：装好对应的 herdr 集成，例如 `herdr integration install codex`，`herdr integration status` 可以查看哪些已装、哪些过期。

**Windows 注意**：npm 装的 codex、kimi、pi 等是通过 `.ps1` 脚本启动的。如果 PowerShell 的执行策略没设置（默认禁止运行脚本），herdr 新开的终端里启动不了它们，`/new` 会报"timed out waiting for agent startup"。用户执行一次 `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` 即可。

## 部署

### 1. 打包

需要 .NET 10 SDK。打出来的程序自带运行时，目标电脑不用装 .NET。

```bash
scripts/publish.sh            # 输出到 artifacts/<平台>/{server,node}/
RIDS="win-x64" scripts/publish.sh   # 只打某个平台
```

### 2. 部署服务器（云服务器，1 核 1G 就够）

香港或海外服务器不用备案；国内服务器用域名加 443 端口需要 ICP 备案。

```bash
# 把 artifacts/linux-x64/server/ 拷到服务器的 /opt/chatpilot/
# 第一次先交互式绑定微信：终端会显示二维码，用手机微信扫码
sudo -u chatpilot env ChatPilot__DataDir=/var/lib/chatpilot /opt/chatpilot/chatpilot-server login
# 再按 deploy/linux/chatpilot-server.service 里的说明注册成服务
```

日志里会打印**证书指纹**（`sha256:…`），节点配对时要用。也可以运行 `chatpilot-server fingerprint` 查看。

配置在 `appsettings.json` 的 `ChatPilot` 节，也可以用环境变量覆盖：

| 配置 | 默认 | 说明 |
|---|---|---|
| `ChatPilot__ListenAddress` | `0.0.0.0` | 不开 TLS 时只能是回环地址 |
| `ChatPilot__Port` | `8443` | |
| `ChatPilot__PublicUrl` | 无 | 节点连接用的地址，会写进 `/pair` 的提示 |
| `ChatPilot__DataDir` | `~/.chatpilot/server` | 状态文件、证书（权限 600） |
| `ChatPilot__Tls__Enabled` | `true` | 自签证书，首次启动自动生成 |
| `ChatPilot__WeChat__Enabled` | `true` | |

### 3. 每台电脑：配对节点

1. 确认 herdr 已经在运行（打开过 herdr 就行）。
2. 在微信里发 `/pair 设备名`，复制返回的命令在电脑上运行，例如：

   ```bash
   chatpilot-node pair --server https://your-server:8443 --code ABCD-EFGH \
     --fingerprint sha256:… --name mac --root ~/Desktop/项目
   ```

   `--root` 是允许 `/new` 打开的目录，可以写多个；不写的话默认是用户主目录。
3. `chatpilot-node doctor` 自检：herdr、允许的目录、agent 类型、服务器连通性。
4. `chatpilot-node run` 运行，或者设置开机自启：
   - macOS：[`deploy/macos/com.chatpilot.node.plist`](deploy/macos/com.chatpilot.node.plist)（自带 `caffeinate`，防止电脑睡眠）
   - Windows：[`deploy/windows/install-node-task.ps1`](deploy/windows/install-node-task.ps1)（登录后启动；不要注册成 Windows 服务）
   - Linux：[`deploy/linux/chatpilot-node.service`](deploy/linux/chatpilot-node.service)

节点配置在 `~/.chatpilot/node.json`（权限 600），里面可以调整：

| 字段 | 说明 |
|---|---|
| `allowedRoots` | `/new` 允许的目录 |
| `defaultAgentKind` | `/new` 默认类型：`reclaude`、`claude`、`codex`… |
| `launchers` | 自定义启动命令，默认带 `reclaude`（运行后等 herdr 识别出 claude） |
| `herdrSession` | 用 herdr 的命名会话（默认用默认会话） |
| `promptWaitHours` | 一个任务最多等多久才放弃通知（默认 6 小时） |

### 本机试用（不用云服务器、不用微信）

```bash
# 终端 1：服务器只听本机，开测试通道
ASPNETCORE_ENVIRONMENT=Development ChatPilot__WeChat__Enabled=false \
ChatPilot__DevChannel__Enabled=true ChatPilot__DevChannel__Token=至少16位的随机字符串 \
  dotnet run --project src/ChatPilot.Server
# 终端 2：模拟手机发消息
curl -s localhost:8080/dev/chat -H 'X-ChatPilot-Dev-Token: 同上' -H 'Content-Type: application/json' -d '{"text":"/pair mac"}'
dotnet run --project src/ChatPilot.Node -- pair --server http://127.0.0.1:8080 --code 上一步的配对码
dotnet run --project src/ChatPilot.Node -- run
```

## 已知限制

- **iLink 不是腾讯公开文档的 API**：协议参照腾讯开源的 openclaw-weixin 插件（MIT），腾讯修改或限制时需要跟着调整。
- **微信不支持真正的主动推送**：ChatPilot 复用你最近一条消息的 context_token 发通知，长时间不说话后能否推送取决于腾讯。
- **回复提取**：能找到会话记录时读记录；否则从终端界面提取，准确度因 agent 而异（见[支持的 agent](#支持的-agent)）。会话记录格式是各 agent 的内部格式，agent 升级后可能需要跟着调整；读不了时会自动退回到读终端。
- 节点或服务器重启时，正在等的任务不再推送结果，用 `/ls`、`/read` 查看。
- 同一个设备令牌只能有一个节点在线：新启动的会把旧的顶掉，被顶掉的那个自动退出。
- 消息按顺序一条一条处理，一条很慢的指令（比如 `/new`）会让后面的消息稍等。
- 不建议放在反向代理后面（限流会变成全局共享），原因见 [`docs/SECURITY.md`](docs/SECURITY.md)。

## 开发

```bash
dotnet build ChatPilot.sln                    # 警告视为错误；src 下公开成员必须有 XML 文档注释
dotnet test tests/ChatPilot.Tests --no-build  # 单元测试 + 集成测试 + 安全测试
```

注释约定：公开和内部成员都写中文 XML 文档注释（`/// <summary>`），句末带句号；方法内部只在不显而易见的地方写行内注释，说明"为什么"而不是复述代码；术语统一用"服务器""节点""设备""agent""主人"。

项目结构：

```
src/ChatPilot.Shared   协议、校验、证书指纹、安全文件写入
src/ChatPilot.Server   服务器：微信接入、设备连接、指令解析与分发
src/ChatPilot.Node     节点：调用 herdr、目录白名单、回复提取、连接与重连
tests/ChatPilot.Tests  测试（模拟 herdr、模拟 iLink、内存服务器）
deploy/                开机自启样例
scripts/publish.sh     打包
```
