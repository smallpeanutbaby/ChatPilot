# ChatPilot 安全说明

ChatPilot 能让手机远程指挥电脑上的 AI agent 写代码、运行命令，本质上是一个**远程控制通道**。这份文档说明保护了什么、怎么保护的，以及还剩哪些风险。

## 1. 要保护的东西

| 资产 | 被拿到会怎样 |
|---|---|
| 你的电脑（通过 agent） | agent 能读写允许目录里的代码、执行命令 |
| 设备令牌 | 冒充你的电脑连服务器（收到你的指令内容） |
| 微信 bot token、登录二维码 | 冒充 ChatPilot 给你发消息；拿到登录二维码的人扫码就能成为"主人" |
| 服务器 | 能给所有已配对电脑下指令 |

## 2. 信任边界

```
手机微信 ──(腾讯 HTTPS)──► 服务器 ◄──(TLS + 指纹)── 节点 ──(本机进程参数)──► herdr ──► agent
          只认主人的微信号        只认已配对令牌         只执行固定操作、参数全部校验
```

## 3. 控制措施

### 3.1 身份和鉴权

| 编号 | 措施 | 实现位置 | 测试 |
|---|---|---|---|
| S-01 | 只处理扫码绑定的那个微信号（主人）发的用户消息，其他人的消息不处理也不回复 | `WeChatChannel.AcceptAsync` | `Owner_messages_are_answered_with_context_token_and_strangers_are_ignored` |
| S-02 | WebSocket 先验令牌再升级；没有令牌或令牌错误返回 401 | `ServerApp.AcceptNodeAsync` | `WebSocket_without_or_with_wrong_token_gets_401_and_is_not_upgraded` |
| S-03 | 配对码：8 位、约 40 bit 随机、10 分钟过期、只能用一次，最多同时 5 个 | `PairingService` | `Code_is_single_use…`、`Codes_expire` |
| S-04 | 配对接口每 IP 每分钟 5 次；WebSocket 令牌错误每 IP 每分钟 10 次（只统计失败，正常重连不受影响），WebSocket 总请求每 IP 每分钟 60 次 | `ServerApp`、`AuthFailureLimiter` | `Pairing_is_rate_limited_per_ip`、`Repeated_bad_tokens_are_throttled…` |
| S-05 | 设备令牌 256 bit 随机数，服务器只存 SHA-256，比较用定长时间算法 | `StateStore.FindDeviceByToken` | `State_file_never_contains_plaintext_tokens…` |
| — | 撤销立即生效：`/unpair` 即使写盘失败也先在内存里删除并断开；握手期间被撤销的设备在登记后会被踢掉；已撤销设备的事件不再转发 | `ChatDispatcher.UnpairAsync`、`DeviceHub` | `Unpair_takes_effect_even_when_saving_fails` |

### 3.2 传输

| 编号 | 措施 | 实现位置 | 测试 |
|---|---|---|---|
| S-06 | 服务器自签证书；节点只认配对时给定的 SHA-256 指纹，https 不带指纹拒绝配对 | `CertificatePin`、`PinnedTransport` | `TlsPinningTests`（真实 Kestrel） |
| S-15 | 不开 TLS 时只能监听回环地址，否则拒绝启动；http 节点只允许连本机 | `ServerOptions.Validate`、`ServerEndpoint` | `Refuses_to_start_without_tls_on_public_interface` |
| S-16 | 协议版本不一致时拒绝连接并说明原因 | `DeviceHub.RunConnectionAsync` | `Protocol_version_mismatch_is_rejected_with_reason` |
| — | 双向心跳超时检测（20 秒），对方睡眠或断网后很快判定离线，不会把指令发给一个已经死掉的连接 | `ServerApp`、`ClientWebSocketConnector` | 真实验收 |

### 3.3 节点：电脑上的安全边界

| 编号 | 措施 | 实现位置 | 测试 |
|---|---|---|---|
| S-07 | `/new` 目录白名单，分两步：先纯字符串检查（展开 `~`、规范化 `..`），不在白名单内的直接拒绝、**不访问文件系统**（避免 Windows 带凭据连 `\\host\share`、macOS 自动挂载 `/net/host`，也不泄露目录是否存在）；再逐级解析符号链接后检查真实路径 | `PathPolicy` | `PathPolicyTests`（含符号链接跳出、`/net/…`） |
| S-08 | 调 herdr 一律用参数数组，不经过 shell；以 `-` 开头的文字加前导空格防止被 herdr 当成选项；子进程去掉 `HERDR_*` 环境变量；herdr 路径不能是 `.cmd` / `.bat` / `.ps1`（Windows 上会经过 cmd.exe） | `HerdrClient`、`ProcessRunner`、`NodeConfig` | `Prompt_text_is_passed_as_a_single_literal_argument`、`Script_wrappers_are_rejected_as_herdr_path` |
| — | 发给 agent 的文字只出现在几秒就结束的提交进程里，等待结果用单独的进程，同机其他用户用 `ps` 看不到指令内容 | `HerdrClient.PromptAsync`、`PromptTracker` | `Session_option_is_added_and_prompt_does_not_keep_text…`、真实验收 |
| S-09 | 协议只有 8 种指令；缺字段、类型不对、嵌套过深都解析失败；结构正常但不认识的指令单独回错误，其他不合规消息断开连接 | `ProtocolJson`、`ServerConnection`、`DeviceHub` | `Unknown_or_malformed_messages_are_rejected`、`Unknown_commands_are_answered_individually…` |
| S-10 | agent 类型只能是 herdr 内置类型或本机配置的启动器；启动器命令来自本机配置文件，不接受远程输入，不能换行 | `NodeConfig`、`CommandExecutor` | `Start_agent_refuses_paths_outside_whitelist_and_unknown_kinds` |
| — | 按键白名单：只有方向键、回车、Esc、Tab、退格、Ctrl+C、y/n、1–9，不能拼出任意文字 | `Validation.AllowedKeys` | `Only_whitelisted_keys_are_allowed` |
| — | `/yes` 先读屏幕找选项再导航，不会盲按回车；只选"这一次同意"，带"不再询问 / 全部允许 / 自动接受"等会扩大权限的选项一律不自动选 | `ApprovalNavigator` | `Never_picks_an_option_that_escalates_permissions`、`Plain_yes_is_preferred` |
| — | `/yes` 只同意给用户看过的确认框（状态序号和确认框内容都要对上），每个只同意一次：迟到或重复的 `/yes` 不会同意用户没看过的新确认框 | `ShownDialogs`、`ApprovalNavigator.Fingerprint` | `Late_yes_does_not_approve_a_dialog_that_replaced_the_one_shown`、`A_shown_dialog_is_approved_at_most_once` |
| — | 节点升级只在用户自己运行 `chatpilot-node update` 时发生，不能从微信远程触发。只从 GitHub Releases 下载（HTTPS），压缩包要和同一个发布里的 `SHA256SUMS.txt` 对上，新程序在本机试运行通过才替换。校验值防的是下载损坏和传输途中被改，防不了仓库本身被攻破，信任程度和手动下载一样。只重启启动命令里正是这个程序的开机自启服务 | `NodeUpdater`、`ServiceRestarter` | `NodeUpdaterTests` |
| — | 校验正则用 `\z` 结尾（.NET 的 `$` 会放过结尾换行） | `Validation` | `Trailing_newline_does_not_bypass_validation` |

### 3.4 资源和健壮性

| 编号 | 措施 | 实现位置 | 测试 |
|---|---|---|---|
| S-11 | WebSocket 单条消息 1 MiB、发给 agent 的文字 16 KB、读取行数 1–500、按键一次最多 20 个、HTTP 请求体 64 KB | `WebSocketMessaging`、`ProtocolInfo` | `Oversized_frames_close_the_connection`、`Oversized_prompt_is_rejected` |
| — | 节点同时跟踪的任务最多 32 个，每个 agent 只有一个后台等待；每台设备的通知每分钟最多 20 条，通知按顺序排队发送 | `PromptTracker`、`DeviceHub`、`Notifier` | `One_watcher_per_agent…`、`Device_events_are_rate_limited_per_minute` |
| — | 每条指令有总时间预算，保证在服务器放弃等待前结束；启动失败（包括超时、断线）一定关掉半启动的工作区 | `CommandExecutor` | `Failed_start_closes_the_new_workspace` |
| — | 主人的消息严格按顺序一条一条处理（先发的 `/use` 一定先生效） | `ChatDispatcher`、`WeChatChannel` | `Messages_in_one_batch_are_handled_strictly_in_order` |
| — | 后台写文件失败（磁盘满、无权限）只记日志，不会让进程崩溃；普通状态修改是事务式的，写盘失败时内存不变 | `StateStore`、`DeviceHub` | `Disk_write_failures_during_disconnect…`、`Failed_writes_leave_memory_unchanged…` |

### 3.5 敏感信息和测试通道

| 编号 | 措施 | 实现位置 | 测试 |
|---|---|---|---|
| S-12 | 测试通道默认不注册路由（404）；开启后只接受回环地址、必须带至少 16 位令牌 | `DevChannel`、`ServerOptions.Validate` | `Dev_channel_is_off_by_default`、`Dev_channel_requires_loopback_and_token` |
| S-13 | 日志里不写令牌、配对码、消息内容，**也不写微信登录链接**；登录二维码保存为权限 600 的图片，字符画只在交互式登录或输出到真实终端时显示 | 全局、`WeChatLogin` | `…logs_do_not_leak_secrets`、`Login_link_is_never_written_to_logs` |
| S-14 | 节点配置、服务器状态文件、证书私钥、登录二维码权限 600；程序新建的目录 700，已有目录（例如用户主目录）不改权限；先落盘再原子替换 | `SecureFile` | `Files_are_written_atomically…`、`Existing_directories_keep_their_permissions` |
| — | 微信登录重定向只接受腾讯域名（`*.qq.com`、`*.weixin.com`、`*.wechat.com`） | `WeChatLogin.IsTrustedHost` | `Only_tencent_hosts_are_trusted_for_redirects` |

## 4. 剩余风险

| 风险 | 说明 | 建议 |
|---|---|---|
| 微信号被盗 | 主人身份就是微信号，微信被盗等于电脑被远程控制 | 微信开启设备锁；不用时可以停掉服务器或 `/unpair` |
| agent 本身权限很大 | 白名单只限制 `/new` 能打开哪里；agent 启动后能做什么由 Claude Code 自己的权限设置决定 | 保持 Claude Code 的权限确认；把 `allowedRoots` 设成具体的项目目录，而不是整个主目录 |
| 服务器被攻破 | 服务器能给所有节点下指令 | 用自己的服务器、及时更新系统、只开放 8443；systemd 样例已做加固 |
| iLink 非公开 API | 腾讯随时可能修改 | 关注 openclaw-weixin 插件的更新 |
| 令牌没有自动过期 | 令牌泄露后一直有效，直到撤销 | 怀疑泄露时 `/unpair` 后重新配对 |
| 代码内容经过服务器 | Claude 的回复会经服务器转发到腾讯 | 不要把服务器交给不信任的人托管 |
| 放在反向代理后面 | 所有请求的来源 IP 都是代理：限流变成全局共享（有人刷错误令牌会影响正常节点重连），测试通道的"只接受本机"检查也会对所有经代理的请求放行（只剩令牌保护） | 直接暴露 Kestrel（默认做法）；必须用代理时在代理层限流，并且不要开测试通道 |
| 证书指纹以明文出现在微信里 | 指纹本身不是秘密，只用来防中间人 | 无需处理 |
