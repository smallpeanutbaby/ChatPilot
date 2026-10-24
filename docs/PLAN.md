# ChatPilot 实现计划

## 1. 目标

用一部手机（微信 ClawBot）同时指挥多台电脑（macOS / Windows）上由 herdr 管理的 AI 编码 agent（主要是 Claude Code）。

- 手机端不写代码，直接用微信。
- 每台电脑装一个**节点服务**，主动连到一台**服务器**；电脑不开端口、不需要公网 IP。
- 服务器只负责转发和整理消息，**自己不带 AI**；理解指令、写代码的都是电脑上的 Claude Code。

## 2. 范围

### 本期要做

| 编号 | 功能 |
|---|---|
| M1 | 微信接入：扫码登录、长轮询收消息、回复（超长文本自动分段）、断线重试、token 失效暂停 |
| M2 | 设备配对：微信里 `/pair` 生成一次性配对码；节点用配对码换取设备凭据；`/unpair` 撤销 |
| M3 | 设备连接：节点通过 WebSocket 连服务器，断线指数退避重连；服务器跟踪设备在线状态 |
| M4 | 指令：`/help` `/devices` `/use` `/ls` `/read` `/new` `/yes` `/no` `/keys` `/name` `/butler` `/pair` `/unpair`；`@目标 内容`；纯文本交给管家 agent |
| M5 | 结果回传：节点等 agent 做完（或卡在审批），把 Claude 的最后一条回复推回微信 |
| M6 | 安全：只认绑定的微信号；设备令牌只存哈希；自签证书 + 指纹校验；配对和连接都限流；`/new` 目录白名单；按键白名单；不开放任意 shell |
| M7 | 开发 / 测试通道：本机回环地址上的 HTTP 接口，模拟微信消息（默认关闭，需要令牌） |
| M8 | 部署：macOS launchd、Windows 登录自启、Linux systemd 的配置样例；单文件发布 |

### 本期不做

- 自己的手机 App / 网页界面（以后接同一个服务器即可）
- 图片、语音、文件消息（只处理文字；语音转文字的内容会当成文字处理）
- 多用户（一个服务器只服务一个微信号）
- 订阅 herdr 全局事件（只跟踪通过 ChatPilot 发出的指令）

## 3. 架构

```
手机微信 ──► 腾讯 iLink ◄── ① ChatPilot.Server（云服务器 / 本机）
                                  ▲  WebSocket（TLS + 指纹校验）
                       ┌──────────┴──────────┐
                ② ChatPilot.Node（Mac）  ② ChatPilot.Node（Windows）
                       │                     │
                     herdr                 herdr
                       │                     │
                  Claude Code …         Claude Code …
```

### 项目结构

```
ChatPilot/
├── global.json                固定 .NET 10 SDK
├── Directory.Build.props      统一编译选项（可空引用、警告视为错误）
├── ChatPilot.sln
├── src/
│   ├── ChatPilot.Shared/      通信协议、JSON 配置、共用工具
│   ├── ChatPilot.Server/      ① 服务器服务（ASP.NET Core）
│   └── ChatPilot.Node/        ② 电脑端服务（控制台 / 后台服务）
├── tests/
│   └── ChatPilot.Tests/       单元测试 + 集成测试（xUnit）
├── deploy/                    launchd / systemd / Windows 计划任务样例
├── scripts/publish.sh         各平台单文件打包
└── docs/                      计划、验收标准、安全说明、验收报告
```

## 4. 关键设计

### 4.1 通信协议（Shared）

- 传输：WebSocket 文本帧，每帧一个 JSON；单帧上限 1 MiB。
- 鉴权：节点升级连接时带 `Authorization: Bearer <设备令牌>`；服务器只存令牌的 SHA-256，比较用定长时间比较。
- 消息类型（`type` 字段区分；`command` 里的具体指令用 `op` 字段区分，结果数据用 `kind` 字段区分）：
  - `hello`（节点→服务器）：协议版本、节点版本、平台、主机名
  - `welcome`（服务器→节点）：设备 ID、设备名
  - `command`（服务器→节点）：`id` + 具体指令（`list_agents` / `prompt` / `read` / `start_agent` / `send_keys` / `respond` / `rename_agent`）
  - `result`（节点→服务器）：`id` + 成功或失败 + 数据
  - `agent_event`（节点→服务器）：agent 做完、卡住或出错，带回复文本
- 版本不一致时服务器拒绝连接并给出原因。

### 4.2 服务器

| 模块 | 职责 |
|---|---|
| `StateStore` | 把状态存到 JSON 文件（原子写入，Unix 下权限 600）：微信账号、绑定的微信号、同步游标、context_token、设备列表 |
| `PairingService` | 生成 8 位配对码（去掉易混字符，约 40 bit），10 分钟有效，只能用一次 |
| `DeviceHub` | 管理在线设备连接；发送指令并等结果（有超时）；撤销设备时断开连接 |
| `ChatCommandParser` | 把文字解析成指令（纯函数，方便测试） |
| `ChatDispatcher` | 执行指令、解析目标（设备 / agent / 序号）、整理回复 |
| `Notifier` | 把 agent 事件推给所有已启用的聊天通道 |
| `ILinkClient` | 腾讯 iLink HTTP 接口：二维码、扫码状态、getupdates、sendmessage |
| `WeChatLogin` | 扫码登录流程（终端显示二维码，同时保存 PNG） |
| `WeChatChannel` | 长轮询主循环：只处理绑定微信号的消息，保存游标和 context_token，分段回复 |
| `DevChannel` | 仅限回环地址、需要令牌的测试接口 |
| `CertificateProvider` | 首次启动生成自签证书，输出 SHA-256 指纹 |

HTTP 接口：

| 路径 | 说明 |
|---|---|
| `GET /healthz` | 健康检查 |
| `POST /api/pair` | 用配对码换设备令牌（按 IP 限流） |
| `GET /ws/node` | 节点 WebSocket（鉴权失败也按 IP 限流） |
| `POST /dev/chat`、`GET /dev/events` | 测试通道（默认关闭） |

### 4.3 节点

| 模块 | 职责 |
|---|---|
| `NodeConfig` | `~/.chatpilot/node.json`（Unix 下权限 600）：服务器地址、设备令牌、证书指纹、herdr 路径、目录白名单等 |
| `HerdrClient` | 用参数数组调用 herdr（不经过 shell，杜绝注入），解析 JSON 结果和错误 |
| `CommandExecutor` | 校验参数（目标格式、按键白名单、目录白名单、agent 类型白名单、长度上限）后执行 |
| `PromptTracker` | 后台等 agent 做完，取回复，通过事件推回去；断线期间先缓存，重连后补发 |
| `ReplyExtractor` | 优先读 Claude 会话记录（jsonl）；没有就从终端输出里提取最后一条回复 |
| `PathPolicy` | 展开 `~`、规范化路径、解析符号链接，检查是否在白名单目录内 |
| `ServerConnection` | 连接、证书指纹校验、hello 握手、收发循环、指数退避重连 |

### 4.4 聊天指令

| 指令 | 作用 |
|---|---|
| `/help` | 帮助 |
| `/devices` | 设备列表和在线状态 |
| `/use <设备>` | 切换默认设备 |
| `/ls [设备]` | 列出 agent，带序号 |
| `@<目标> <内容>` | 把内容交给 agent；目标可以是 `序号`、`名字`、`pane ID`、`工作区名`，可加设备前缀 `设备/目标` |
| `/read <目标> [行数]` | 读 agent 最近的输出 |
| `/new [设备] <目录> [类型] [名字]` | 在白名单目录里新开工作区并启动 agent（默认 claude） |
| `/yes <目标>`、`/no <目标>` | 处理审批（回车 / Esc） |
| `/keys <目标> <键…>` | 发送白名单内的按键 |
| `/name <目标> <新名字>` | 给 agent 改名 |
| `/butler [设备]` | 在设备上创建管家 agent |
| `/pair [设备名]`、`/unpair <设备>` | 配对 / 撤销设备 |
| 其他文字 | 交给当前设备上名为 `butler` 的管家 agent |

## 5. 安全设计（详见 `SECURITY.md`）

1. **身份**：首次扫码的微信号就是唯一主人，其他人发的消息一律忽略。
2. **设备**：配对码随机、一次性、10 分钟过期；设备令牌 256 bit 随机数，服务器只存哈希；可以随时撤销。
3. **传输**：TLS（自签证书）+ 节点校验证书指纹；不开 TLS 时只允许监听回环地址（开发用）。
4. **限流**：配对接口和 WebSocket 鉴权失败都按 IP 限流。
5. **节点权限最小化**：只执行固定的几种 herdr 操作；参数全部校验；用参数数组启动进程；目录必须在白名单内；按键只能用白名单里的。
6. **输入上限**：WebSocket 单帧 1 MiB，指令文本 16 KB，读取行数 1–500。
7. **敏感信息**：日志里不输出令牌；配置和状态文件权限 600。
8. **测试通道**：默认关闭；只接受回环地址；需要令牌。

## 6. 里程碑

| 阶段 | 内容 | 产出 |
|---|---|---|
| P0 | 计划和验收标准 | `docs/PLAN.md`、`docs/ACCEPTANCE.md` |
| P1 | 搭项目骨架和协议 | Shared + 编译通过 |
| P2 | 节点：herdr 调用、指令执行、回复提取、连接 | Node + 单元测试 |
| P3 | 服务器：存储、配对、设备连接、指令解析和分发、测试通道 | Server + 集成测试 |
| P4 | 微信通道：iLink 客户端、扫码登录、长轮询 | 用模拟的 iLink 服务器测试 |
| P5 | 部署样例、使用说明 | `deploy/`、`README.md` |
| P6 | 代码审查 | 修完审查发现的问题 |
| P7 | 功能验收和安全测试 | `docs/ACCEPTANCE-REPORT.md` |

## 7. 风险

| 风险 | 应对 |
|---|---|
| iLink 不是公开 API，腾讯可能修改或限制 | 微信接入单独封装；聊天通道可替换（以后可以加飞书 / Telegram） |
| 微信不能主动推送消息 | 复用最近一次的 context_token 回复；推送失败记录日志 |
| 从终端提取回复不够准 | 优先读会话记录；建议安装 herdr 的 Claude 集成（`herdr integration install claude`） |
| herdr 在 Windows 上的差异 | 只依赖文档里写明的 CLI；路径比较考虑大小写 |
| 节点或服务器重启后，正在等的任务丢失 | 本期接受这个限制；重启后用 `/ls`、`/read` 查看状态 |

## 8. 第二期

第一期"本期不做"里的图片消息、订阅 herdr 全局事件，以及风险里提到的备用聊天通道，在第二期做了。验收标准见 `ACCEPTANCE.md` 的 F-40 到 F-53、S-17 到 S-21。

| 功能 | 放在哪一层 | 要点 |
|---|---|---|
| 不写目标 / `/re` | 服务器 `ConversationState` | 记住最近打过交道的 agent 和还没处理的确认框；`/yes` 有多个候选时不猜 |
| 排队 | 节点 `PromptQueue`、`PromptTracker` | agent 忙时排队，每个 pane 一个后台泵，空闲且没有别的跟踪时逐条发出；存盘，重启后接着发 |
| `/stop`、`/close` | 节点 `CommandExecutor` + 服务器确认 | `/stop` 只在工作中按 Esc；`/close` 服务器端两步确认、pane 对得上才关 |
| `/watch` | 节点 `AgentWatcher` | 有盯着的 agent 时轮询 `agent list` 看状态变化；跟踪方上报过的不重复通知 |
| `/diff` | 节点 `GitInspector` | 只读 git 命令，关掉外部 diff / textconv / fsmonitor |
| `/pending`、`/history` | 服务器 | 列表数据来自节点（排队数、任务开始时间）；记录只存摘要 |
| `/at` | 服务器 `Scheduler` | 存在状态文件里；先删后执行，错过太久不执行；按 `TimeZone` 计算时刻 |
| 图片 | 服务器 `MediaStore` + 节点 `AttachmentDownloader` | 微信 CDN 下载并 AES-128-ECB 解密；节点凭设备令牌从服务器取，存到 `.chatpilot/attachments/` |
| `/wake` | 节点 `LocalNetwork` | 握手时报告网卡，服务器挑同一网段的在线设备广播唤醒包 |
| Telegram | 服务器 `TelegramChannel` | 和微信并列的聊天通道，只认配置的主人，通知两边都发 |

协议版本号没有变：新增的字段都是可选的，旧节点收到不认识的指令会单独回"请升级节点"，不会断开连接。
