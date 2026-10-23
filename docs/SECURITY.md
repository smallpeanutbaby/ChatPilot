# ChatPilot 安全说明

ChatPilot 能让手机远程指挥电脑上的 AI agent 写代码、运行命令，本质上是一个**远程控制通道**。这份文档说明保护了什么、怎么保护的，以及还剩哪些风险。

## 1. 要保护的东西

| 资产 | 被拿到会怎样 |
|---|---|
| 你的电脑（通过 agent） | agent 能读写允许目录里的代码、执行命令 |
| 设备令牌 | 冒充你的电脑连服务器（收到你的指令内容） |
| 微信 bot token | 冒充 ChatPilot 给你发消息、读你发给 bot 的消息 |
| 服务器 | 能给所有已配对电脑下指令 |

## 2. 信任边界

```
手机微信 ──(腾讯 HTTPS)──► 服务器 ◄──(TLS + 指纹)── 节点 ──(本机进程参数)──► herdr ──► agent
          只认主人的微信号        只认已配对令牌         只执行固定操作、参数全部校验
```

## 3. 控制措施

| 编号 | 措施 | 实现位置 | 测试 |
|---|---|---|---|
| S-01 | 只处理扫码绑定的那个微信号（主人）发的用户消息，其他人的消息不处理也不回复 | `WeChatChannel.AcceptAsync` | `Owner_messages_are_answered_with_context_token_and_strangers_are_ignored` |
| S-02 | WebSocket 先验令牌再升级；没有令牌或令牌错误返回 401 | `ServerApp.AcceptNodeAsync` | `WebSocket_without_or_with_wrong_token_gets_401_and_is_not_upgraded` |
| S-03 | 配对码：8 位、约 40 bit 随机、10 分钟过期、只能用一次，最多同时 5 个 | `PairingService` | `Code_is_single_use…`、`Codes_expire` |
| S-04 | 配对接口每 IP 每分钟 5 次，WebSocket 每 IP 每分钟 30 次 | `ServerApp` 限流策略 | `Pairing_is_rate_limited_per_ip` |
| S-05 | 设备令牌 256 bit 随机数，服务器只存 SHA-256，比较用定长时间算法 | `StateStore.FindDeviceByToken` | `State_file_never_contains_plaintext_tokens…` |
| S-06 | 服务器自签证书；节点只认配对时给定的 SHA-256 指纹，https 不带指纹拒绝配对 | `CertificatePin`、`PinnedTransport` | `TlsPinningTests`（真实 Kestrel） |
| S-07 | `/new` 目录白名单：展开 `~`、规范化 `..`、逐级解析符号链接后比较真实路径；拒绝相对路径、不存在的目录、NUL 字符；防"前缀相同的兄弟目录" | `PathPolicy` | `PathPolicyTests`（含符号链接跳出） |
| S-08 | 调 herdr 一律用参数数组，不经过 shell；以 `-` 开头的文字加前导空格防止被 herdr 当成选项；子进程去掉 `HERDR_*` 环境变量 | `HerdrClient`、`ProcessRunner` | `Prompt_text_is_passed_as_a_single_literal_argument`、真实 `/bin/echo` 测试 |
| S-09 | 协议只有 7 种指令，未知类型、缺类型、嵌套过深都解析失败；节点发来服务器端消息类型直接断开 | `ProtocolJson`、`DeviceHub` | `Unknown_or_malformed_messages_are_rejected`、`Garbage_and_node_side_forbidden_messages…` |
| S-10 | agent 类型只能是 herdr 内置类型或本机配置的启动器；启动器命令来自本机配置文件，不接受远程输入，不能换行 | `NodeConfig`、`CommandExecutor` | `Start_agent_refuses_paths_outside_whitelist_and_unknown_kinds` |
| S-11 | WebSocket 单条消息 1 MiB、发给 agent 的文字 16 KB、读取行数 1–500、按键一次最多 20 个、HTTP 请求体 64 KB | `WebSocketMessaging`、`ProtocolInfo` | `Oversized_frames_close_the_connection`、`Oversized_prompt_is_rejected` |
| S-12 | 测试通道默认不注册路由（404）；开启后只接受回环地址、必须带至少 16 位令牌 | `DevChannel`、`ServerOptions.Validate` | `Dev_channel_is_off_by_default`、`Dev_channel_requires_loopback_and_token` |
| S-13 | 日志里不写令牌、配对码、消息内容 | 全局 | `…logs_do_not_leak_secrets` |
| S-14 | 节点配置、服务器状态文件、证书私钥权限 600，目录 700，原子替换（先建 600 的临时文件再改名） | `SecureFile` | `Files_are_written_atomically_with_owner_only_permissions` |
| S-15 | 不开 TLS 时只能监听回环地址，否则拒绝启动；http 节点只允许连本机 | `ServerOptions.Validate`、`ServerEndpoint` | `Refuses_to_start_without_tls_on_public_interface` |
| S-16 | 协议版本不一致时拒绝连接并说明原因 | `DeviceHub.RunConnectionAsync` | `Protocol_version_mismatch_is_rejected_with_reason` |
| — | 按键白名单：只有方向键、回车、Esc、Tab、退格、Ctrl+C、y/n、1–9，不能拼出任意文字 | `Validation.AllowedKeys` | `Only_whitelisted_keys_are_allowed` |
| — | `/yes` 先读屏幕找"Yes"类选项再导航，找不到就拒绝，不会盲按回车（避免在"信任目录"框里误选 No, exit 或误点其他选项） | `ApprovalNavigator` | `ApprovalNavigatorTests` |
| — | 微信登录重定向只接受腾讯域名（`*.qq.com`、`*.weixin.com`、`*.wechat.com`） | `WeChatLogin.IsTrustedHost` | `Only_tencent_hosts_are_trusted_for_redirects` |
| — | 后台写文件失败（磁盘满、无权限）只记日志，不会让进程崩溃 | `DeviceHub.TouchLastSeen` | `Disk_write_failures_during_disconnect_do_not_crash_the_server` |

## 4. 剩余风险

| 风险 | 说明 | 建议 |
|---|---|---|
| 微信号被盗 | 主人身份就是微信号，微信被盗等于电脑被远程控制 | 微信开启设备锁；不用时可以停掉服务器或 `/unpair` |
| agent 本身权限很大 | 白名单只限制 `/new` 能打开哪里；agent 启动后能做什么由 Claude Code 自己的权限设置决定 | 保持 Claude Code 的权限确认；把 `allowedRoots` 设成具体的项目目录，而不是整个主目录 |
| 服务器被攻破 | 服务器能给所有节点下指令 | 用自己的服务器、及时更新系统、只开放 8443；systemd 样例已做加固 |
| iLink 非公开 API | 腾讯随时可能修改 | 关注 openclaw-weixin 插件的更新 |
| 证书指纹以明文出现在微信里 | 指纹本身不是秘密，只用来防中间人 | 无需处理 |
| 令牌没有自动过期 | 令牌泄露后一直有效，直到撤销 | 怀疑泄露时 `/unpair` 后重新配对 |
| 代码内容经过服务器 | Claude 的回复会经服务器转发到腾讯 | 不要把服务器交给不信任的人托管 |
| 反向代理后的限流 | 放在反向代理后面时，所有请求的来源 IP 都是代理 | 直接暴露 Kestrel（默认做法），或在代理层限流 |
