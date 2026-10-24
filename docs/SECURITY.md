# ChatPilot 安全说明

ChatPilot 能让手机远程指挥电脑上的 AI agent 写代码、运行命令，本质上是一个**远程控制通道**。这份文档说明保护了什么、怎么保护的，以及还剩哪些风险。

## 1. 要保护的东西

| 资产 | 被拿到会怎样 |
|---|---|
| 你的电脑（通过 agent） | agent 能读写允许目录里的代码、执行命令 |
| 设备令牌 | 冒充你的电脑连服务器（收到你的指令内容） |
| 微信 bot token、登录二维码 | 冒充 ChatPilot 给你发消息；拿到登录二维码的人扫码就能成为"主人" |
| Telegram bot token（启用 Telegram 时） | 冒充机器人给你发消息、抢先收走你发给机器人的指令（但发不出能被执行的指令，见 S-21） |
| 你发的图片、任务记录 | 图片和任务摘要会经过服务器，暂存在服务器内存 / 数据目录里 |
| 服务器 | 能给所有已配对电脑下指令 |

## 2. 信任边界

```
手机微信 ──(腾讯 HTTPS)──►┐
Telegram ──(HTTPS)──────►├ 服务器 ◄──(TLS + 指纹)── 节点 ──(本机进程参数)──► herdr ──► agent
  只认主人（微信号 / 用户 ID）       只认已配对令牌         只执行固定操作、参数全部校验
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
| S-21 | Telegram 通道只处理配置的 `OwnerUserId` 在**私聊**里发的消息；陌生人的消息不处理不回复，日志里只记对方的用户 ID（方便第一次配置），不记内容。bot token 是 API 地址的一部分，所以这个通道出的错误一律不带地址；Telegram 通道和通知器（`Notifier`）的日志只记异常的类型和说明，不打印异常对象（里面套着的异常可能带着地址）；访问 Telegram 不用会记录请求地址的 `IHttpClientFactory`。停机、代理不通期间积压的消息，晚到超过 10 分钟的不执行、只告诉主人（微信消息带发送时间时也一样），迟到的 `/yes` 不会同意后来才出现的确认框 | `TelegramChannel.IsFromOwner`、`TelegramClient.CallAsync`、`Notifier.SendAsync`、`StaleCommands` | `Only_the_owner_is_answered_and_the_offset_is_saved`、`Failed_notifications_log_only_the_error_type_and_message`、`Messages_that_arrive_too_late_are_reported_instead_of_run` |
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
| S-09 | 协议只有 14 种指令；缺字段、类型不对、嵌套过深都解析失败；结构正常但不认识的指令单独回错误，其他不合规消息断开连接 | `ProtocolJson`、`ServerConnection`、`DeviceHub` | `Unknown_or_malformed_messages_are_rejected`、`Unknown_commands_are_answered_individually…` |
| S-10 | agent 类型只能是 herdr 内置类型或本机配置的启动器；启动器命令来自本机配置文件，不接受远程输入，不能换行 | `NodeConfig`、`CommandExecutor` | `Start_agent_refuses_paths_outside_whitelist_and_unknown_kinds` |
| S-17 | `/diff` 只运行固定的只读 git 命令（`rev-parse`、`status`、`diff`、`log`），参数数组、不经过 shell。仓库配置能让 git 执行外部程序的口子能关的都关掉：`--no-ext-diff`、`--no-textconv`、`core.fsmonitor=false`、`--no-show-signature`（不运行 `gpg.program` 验证签名）、`--submodule=short` 和 `--ignore-submodules=dirty`（不在子模块里再起 git 进程，子模块的配置也就用不上；代价是子模块里没提交的改动不显示）（仓库里配置的 clean 过滤器 git 没法关，见剩余风险）；`--no-optional-locks` 不改写索引；`--literal-pathspecs` 让文件参数按字面解释；文件路径必须是相对路径，不能有 `..`、盘符、开头的 `-` 和控制字符，两边都校验；git 的输出最多读 800 多万字，超过时结束进程、只返回前面一部分 | `GitInspector`、`Validation.IsRepoRelativePath`、`ProcessRunner` | `GitInspectorTests`、`Repo_relative_paths`、`Real_process_runner_stops_reading_and_kills_a_process_with_endless_output` |
| S-18 | `/close` 分两步：服务器先回一条说明（哪台设备、哪个 agent、目录），只有 2 分钟内带"确认"再发一次、并且解析到的还是同一个 pane 才真正关闭。`/stop` 只在 agent 工作中时按 Esc，在等确认或空闲时不按任何键 | `ChatDispatcher.CloseAsync`、`CommandExecutor.StopAsync` | `Close_confirmation_without_a_fresh_preview_only_previews`、`Stop_presses_esc_only_while_working…` |
| S-19 | `/wake` 节点只广播 Wake-on-LAN 唤醒包，MAC 地址格式两边都校验（`AA:BB:CC:DD:EE:FF`，最多 8 个），没有其他网络操作；服务器记下的网卡信息也先校验再保存 | `CommandExecutor.WakeAsync`、`DeviceHub.SanitizeNetworks` | `Mac_addresses`、`Reported_networks_are_sanitized` |
| S-20 | 图片：主人发的图片只在服务器内存里暂存 10 分钟；派活时为目标设备生成 128 bit 随机、只能用一次的下载 ID，节点要带**这台设备**的令牌才能下载（别的设备的令牌返回 404，错误令牌按 IP 限流）；节点核对大小和 SHA-256，只接受 PNG / JPG / GIF / WebP / BMP（按文件头判断），文件名由节点生成，不用任何外来的名字；存到 agent 工作目录的 `.chatpilot/attachments/`，目录里放一个内容为 `*` 的 .gitignore，图片不会进 git；`.chatpilot`、它下面的 `.gitignore` 或 `attachments` 是链接（可能指到 agent 目录外面）时不跟着写，改存到节点自己的目录。超过 7 天的旧图片、没发出去就被清掉的排队指令带的图片会删掉（只删文件名是节点自己起的）。微信图片的完整下载地址只接受 https 的 `*.qq.com` | `MediaStore`、`ServerApp.FetchMedia`、`AttachmentDownloader`、`ILinkClient.ImageUri` | `Media_downloads_require_the_token_of_the_device_it_was_issued_to`、`AttachmentDownloaderTests`、`Owner_images_are_downloaded_from_the_cdn_decrypted_and_staged` |
| — | 按键白名单：只有方向键、回车、Esc、Tab、退格、Ctrl+C、y/n、1–9，不能拼出任意文字 | `Validation.AllowedKeys` | `Only_whitelisted_keys_are_allowed` |
| — | `/yes` 先读屏幕找选项再导航，不会盲按回车；只选"这一次同意"，带"不再询问 / 全部允许 / 自动接受"等会扩大权限的选项一律不自动选 | `ApprovalNavigator` | `Never_picks_an_option_that_escalates_permissions`、`Plain_yes_is_preferred` |
| — | `/yes` 只同意给用户看过的确认框（状态序号和确认框内容都要对上），每个只同意一次：迟到或重复的 `/yes` 不会同意用户没看过的新确认框 | `ShownDialogs`、`ApprovalNavigator.Fingerprint` | `Late_yes_does_not_approve_a_dialog_that_replaced_the_one_shown`、`A_shown_dialog_is_approved_at_most_once` |
| — | 节点升级只在用户自己运行 `chatpilot-node update` 时发生，不能从微信远程触发。只从 GitHub Releases 下载（HTTPS），压缩包要和同一个发布里的 `SHA256SUMS.txt` 对上，新程序在本机试运行通过才替换。校验值防的是下载损坏和传输途中被改，防不了仓库本身被攻破，信任程度和手动下载一样。只重启启动命令里有一个参数正是这个程序（两边都解析符号链接后按完整路径比较，只是包含这个路径不算）、而且正在运行的开机自启服务，用户自己停掉的不会被打开；调用的系统命令（PowerShell、launchctl、systemctl、id、sudo）都用绝对路径，不会运行当前目录里的同名程序；用 sudo 升级时重启的是原来那个用户的服务；同一个程序同时只能有一个升级在进行 | `NodeUpdater`、`ServiceRestarter` | `NodeUpdaterTests`、`ServiceRestarterTests` |
| — | 校验正则用 `\z` 结尾（.NET 的 `$` 会放过结尾换行） | `Validation` | `Trailing_newline_does_not_bypass_validation` |

### 3.4 资源和健壮性

| 编号 | 措施 | 实现位置 | 测试 |
|---|---|---|---|
| S-11 | WebSocket 单条消息 1 MiB、发给 agent 的文字 16 KB、读取行数 1–500、按键一次最多 20 个、HTTP 请求体 64 KB；图片单张 10 MB、暂存最多 4 张 | `WebSocketMessaging`、`ProtocolInfo`、`MediaStore` | `Oversized_frames_close_the_connection`、`Oversized_prompt_is_rejected` |
| — | 排队的指令每个 agent 最多 10 条、全部最多 50 条，排太久（超过任务等待上限）的丢弃并通知；盯梢只在有盯着的 agent 时才调用 herdr；定时任务最多 20 个，先从状态文件删掉再执行（崩溃后不会重复执行），删除和执行跟 `/at del` 在同一个排队区段里（回复已取消的不会再执行），服务器停机错过超过 10 分钟的不再执行、只通知 | `PromptQueue`、`AgentWatcher`、`Scheduler`、`ChatDispatcher.TakeScheduledJobAsync` | `QueueAndWatchTests`、`Due_jobs_run_like_a_message_and_missed_ones_are_reported`、`A_job_reported_as_cancelled_never_runs` |
| — | 节点同时跟踪的任务最多 32 个，每个 agent 只有一个后台等待；每台设备的通知每分钟最多 20 条，通知按顺序排队发送 | `PromptTracker`、`DeviceHub`、`Notifier` | `One_watcher_per_agent…`、`Device_events_are_rate_limited_per_minute` |
| — | 同一份配置同时只能运行一个节点（配置旁边的 `node.lock`，进程退出时系统自动释放）：第二个节点最多等 20 秒，等不到就以 0 退出，不会两个节点一起发排队的指令、互相覆盖状态文件。升级重启时先确认旧节点退出了（Windows 上结束 `Stop-ScheduledTask` 留下来的节点进程）。下载升级包时 60 秒没收到数据或总共超过 30 分钟就放弃，不会一直卡住 | `NodeCli.AcquireInstanceLockAsync`、`ExclusiveFileLock`、`ServiceRestarter`、`NodeUpdater` | `SingleInstanceTests`、`Windows_restart_also_stops_the_node_left_behind…`、`Stalled_download_gives_up_instead_of_hanging` |
| — | 每条指令有总时间预算，保证在服务器放弃等待前结束；启动失败（包括超时、断线）一定关掉半启动的工作区 | `CommandExecutor` | `Failed_start_closes_the_new_workspace` |
| — | 主人的消息严格按顺序一条一条处理（先发的 `/use` 一定先生效） | `ChatDispatcher`、`WeChatChannel` | `Messages_in_one_batch_are_handled_strictly_in_order` |
| — | 后台写文件失败（磁盘满、无权限）只记日志，不会让进程崩溃；普通状态修改是事务式的，写盘失败时内存不变 | `StateStore`、`DeviceHub` | `Disk_write_failures_during_disconnect…`、`Failed_writes_leave_memory_unchanged…` |

### 3.5 敏感信息和测试通道

| 编号 | 措施 | 实现位置 | 测试 |
|---|---|---|---|
| S-12 | 测试通道默认不注册路由（404）；开启后只接受回环地址、必须带至少 16 位令牌 | `DevChannel`、`ServerOptions.Validate` | `Dev_channel_is_off_by_default`、`Dev_channel_requires_loopback_and_token` |
| S-13 | 日志里不写令牌、配对码、消息内容，**也不写微信登录链接**；登录二维码保存为权限 600 的图片，字符画只在交互式登录或输出到真实终端时显示 | 全局、`WeChatLogin` | `…logs_do_not_leak_secrets`、`Login_link_is_never_written_to_logs` |
| S-14 | 节点配置、服务器状态文件、证书私钥、登录二维码权限 600；程序新建的目录 700，已有目录（例如用户主目录）不改权限；先落盘再原子替换 | `SecureFile` | `Files_are_written_atomically…`、`Existing_directories_keep_their_permissions` |
| — | 节点握手时报告有线 / 无线网卡的 MAC 地址和内网 IPv4 网段（只用于 `/wake`），服务器校验后存在 `state.json`（权限 600），不出现在日志里 | `LocalNetwork.Describe`、`DeviceHub.SanitizeNetworks` | `Nodes_report_their_network_cards_when_they_connect` |
| — | 新增的本地文件同样权限 600：节点的 `node.queue.json`（排队的指令原文）、`node.watch.json`；服务器的 `history.json`（任务和结果各只存前 80 字的摘要） | `PromptQueue`、`AgentWatcher`、`TaskHistory` | `QueueAndWatchTests`、`History_lists_finished_tasks_with_today_totals` |
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
| `/diff` 会运行仓库配置的 clean 过滤器 | git 没有关掉过滤器的开关：`.git/config` 里配了 `filter.*.clean` / `process`、并且属性文件把文件指向它时，`git status` / `git diff` 会运行它，和你自己在这个目录里运行 `git status` 完全一样。这些配置只能由能写这台电脑的人（包括 agent 自己）设置，服务器和手机改不了 | 不要对来历不明的仓库用 `/diff`；agent 本身能执行命令，这不是新增的能力 |
| Telegram bot token 泄露 | 拿到 token 的人能冒充机器人给你发消息，也能抢先调用 getUpdates 把你发的指令收走（导致指令没执行）；但伪造不了"主人发来的消息"，所以不能借此控制电脑 | token 用环境变量配置；怀疑泄露时在 @BotFather 里 /revoke 换一个 |
| 盯梢和定时任务扩大了"自动"范围 | 盯梢只发通知、不按键；定时任务到点执行的是主人自己写好的那条消息，发给创建时就定下的设备和 agent（名字），不会因为到点时默认设备换了、别处有同名 agent 而发错 | `/at` 查看、`/at del` 取消；不需要时 `/unwatch all` |
