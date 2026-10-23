# ChatPilot 验收报告

- 日期：2026-10-22
- 环境：macOS 26.5.1（arm64），.NET SDK 10.0.101，herdr 0.9.5，Claude Code 2.1.308（通过 reclaude 和 claude 两种方式启动）
- 对照标准：[`ACCEPTANCE.md`](ACCEPTANCE.md)

验证方式说明：

- **自动**：`dotnet test` 中的测试（括号里是测试名）
- **本机**：在本机用**打包后的程序**实测。服务器开 TLS 自签证书（127.0.0.1:18443），节点通过配对接入；herdr 使用隔离的命名会话 `cptest`，里面运行真实的 Claude Code，不影响日常使用的 agent。手机消息通过测试通道模拟
- **人工**：需要你在手机微信上操作，步骤见第五节

## 一、结论

| 类别 | 结果 |
|---|---|
| 构建和质量（Q） | 4/4 通过 |
| 功能（F） | 自动 + 本机项全部通过；F-30、F-36 中需要扫码的部分待人工确认 |
| 安全（S） | 16/16 通过 |
| 部署（D） | 2/2 通过 |

自动测试：**252 个全部通过**（先 `dotnet build`，再 `dotnet test --no-build`，连续多轮结果一致，没有中断）。

代码审查：独立审查者（只拿到代码和需求，看不到实现时的推理）提出 18 条问题和 4 条存疑项，全部核实后修复或说明，见第七节。修复后在真实环境重新验收。

## 二、构建和质量

| 编号 | 结果 | 证据 |
|---|---|---|
| Q-01 | ✅ | `dotnet build ChatPilot.sln`：0 警告 0 错误（`TreatWarningsAsErrors=true`）；源码项目开启文档检查，公开成员缺 XML 注释会直接编译失败 |
| Q-02 | ✅ | 252/252 通过 |
| Q-03 | ✅ | `scripts/publish.sh` 生成 osx-arm64、linux-x64、win-x64 单文件程序（自带运行时，70–106 MB），`file` 确认分别为 Mach-O arm64、ELF x86-64、PE32+；本机验收用的就是 osx-arm64 打包产物 |
| Q-04 | ✅ | 全部项目开启可空引用检查；源码和测试中没有 `!` 空值压制 |

## 三、功能

| 编号 | 结果 | 证据 |
|---|---|---|
| F-01 | ✅ | 自动（`Pair_command_returns_code_and_instructions`）；本机：`/pair macbook` 返回配对码、10 分钟有效期、证书指纹和完整的节点命令 |
| F-02 | ✅ | 本机：复制 `/pair` 返回的命令运行，配对成功，配置文件权限 `-rw-------`，自动检测到 reclaude 并设为默认 |
| F-03 | ✅ | 自动（`Device_goes_online_and_offline`）；本机：节点运行后 `/devices` 显示 🟢，节点收到 SIGTERM 正常退出（退出码 0）后显示 ⚪ |
| F-04 | ✅ | 本机：停掉服务器，节点按 1→2→4→8→16 秒退避重连；服务器重启后自动重连，证书指纹不变，不用重新配对 |
| F-05 | ✅ | 自动（`Unpair_disconnects_and_revokes_token`）；本机：`/unpair macbook` 后节点立刻断开，重连被拒绝，以退出码 2 退出 |
| F-06 | ✅ | 自动（`Second_connection_replaces_first`） |
| F-10 | ✅ | 自动 + 本机：`/ls` 按设备分组，显示序号、名字或工作区、类型、状态和终端标题 |
| F-11 | ✅ | 自动（`List_use_prompt_and_completion_notification`） |
| F-12 | ✅ | 自动（`Targets_resolve_by_name_pane_label_and_device_prefix`，5 种写法）；本机：`@1`、`@demo`、`@demo2` |
| F-13 | ✅ | 自动（`Ambiguous_missing_and_offline_targets_are_explained`）：两台设备都有 `web` 时提示歧义，不发给任何一方 |
| F-14 | ✅ | 同上：`设备/目标` 指定设备；设备离线时提示 |
| F-15 | ✅ | 自动（`Long_replies_are_split_into_at_most_four_messages`）；本机：真实 Claude 回复"391 验收通过"、"README.md 的第一行是 # demo…"被推回，工具调用被跳过，界面空白和提示被清理 |
| F-16 | ✅ | 本机：reclaude 启动后卡在"信任此目录"，屏幕摘要和处理方法被推回；`/yes macbook/demo` 自动从"No, exit"下移到"Yes, I trust this folder"并确认 |
| F-17 | ✅ | 自动 + 本机：`/read 1 8` 返回去掉输入框和空白后的最近内容；行数超出 1–500 时拒绝 |
| F-18 | ✅ | 自动 + 本机：`/new <目录> reclaude demo` 新建工作区、运行 reclaude、herdr 识别为 claude 后改名；白名单外的 `/etc` 和 `..` 路径穿越被拒绝 |
| F-19 | ✅ | 自动 + 本机：`/keys demo2 ctrl+d` 被拒绝并列出允许的按键；`/name demo demo2` 成功 |
| F-20 | ✅ | 自动 + 本机：没有管家时提示用 `/butler` 创建；有管家后普通文字交给管家 |
| F-21 | ✅ | 本机：`/butler` 创建管家，收到"管家已就绪"；问"有哪些 agent"，管家自己调用 herdr 查到 3 个 agent 并整理回复。审查修复后复测：在从未信任过的目录创建管家，卡在"信任此目录"，20 秒后才 `/yes`，初始说明依然送达 |
| F-22 | ✅ | 自动（`Bad_input_returns_usage_not_errors`、`ChatCommandParserTests` 24 例） |
| F-23 | ✅ | 自动（`Node_timeouts_do_not_hang_the_server`）：节点 8 秒不响应时，5 秒返回超时提示，服务器照常响应 |
| F-30 | ✅ 自动 / ⏳ 人工 | 自动（`Handles_refresh_redirect_and_verify_code_then_saves_owner`）：显示二维码、保存 PNG、保存 bot token 和主人微信号。真实扫码待人工 |
| F-31 | ✅ | 自动：二维码过期刷新、最多 3 次、IDC 重定向、需要验证码（交互 / 非交互两种） |
| F-32 | ✅ | 自动（模拟 iLink）：主人消息得到回复，回复带上该消息的 context_token、Bearer token 和 bot_agent |
| F-33 | ✅ | 自动：同步游标持久化，重启后从上次位置继续 |
| F-34 | ✅ | 自动（`Stale_token_pauses_for_an_hour_and_repeated_failures_back_off`，模拟时钟）：-14 暂停 1 小时；连续 3 次失败退避 30 秒 |
| F-35 | ✅ | 自动：`message_id = 18446744073709551615` 不丢精度 |
| F-36 | ⏳ 人工 | 见第五节 |

## 四、安全

| 编号 | 结果 | 证据 |
|---|---|---|
| S-01 | ✅ | 自动：陌生人发 `/pair` 不处理、不回复 |
| S-02 | ✅ | 自动：不带 / 错误令牌返回 401，不升级连接 |
| S-03 | ✅ | 自动 + 本机：同一个配对码第二次使用被拒绝 |
| S-04 | ✅ | 自动：同一 IP 第 6 次返回 429，其他 IP 不受影响 |
| S-05 | ✅ | 自动 + 本机：状态文件里查不到令牌明文 |
| S-06 | ✅ | 自动（真实 Kestrel）+ 本机：错误指纹配对失败；https 不带指纹拒绝配对；指纹正确但令牌错误时得到 401 |
| S-07 | ✅ | 自动（13 例，含符号链接跳出、前缀相同的兄弟目录）+ 本机 |
| S-08 | ✅ | 自动（含真实 `/bin/echo` 验证不经过 shell）+ 本机：Claude 会话记录里收到的原文是 `' --help; $(whoami) \`id\` …'`，特殊字符原样保留，开头加了防选项的空格 |
| S-09 | ✅ | 自动：未知指令、缺类型、非 JSON、嵌套过深解析失败；节点发服务器端消息被断开 |
| S-10 | ✅ | 自动：`bash` 类型被拒绝，工作区不会被创建 |
| S-11 | ✅ | 自动：超过 1 MiB 的消息断开连接；超过 16 KB 的文字拒绝 |
| S-12 | ✅ | 自动：默认 404；非本机 403；令牌错误 401 |
| S-13 | ✅ | 自动：完整流程后日志里没有令牌、配对码、测试通道令牌 |
| S-14 | ✅ | 自动 + 本机：`node.json`、`state.json`、`server.pfx` 都是 600 |
| S-15 | ✅ | 自动：不开 TLS 且监听 0.0.0.0 时拒绝启动 |
| S-16 | ✅ | 自动：协议版本 99 被拒绝，关闭原因写明版本不一致 |

## 五、待你在手机上确认（F-30、F-36）

1. 服务器上运行 `chatpilot-server login`，用手机微信扫码；如果手机上显示数字，在终端输入
2. 启动服务器，在微信里给 ClawBot 发 `/help`，应该收到指令列表
3. 发 `/pair mac`，在电脑上运行返回的命令，再运行 `chatpilot-node run`
4. 依次发 `/devices`、`/ls`、`@1 只回复：收到`，几秒后应收到"✅ … 做完了：收到"

## 六、验收中发现并修复的问题

| # | 问题 | 怎么发现的 | 修复 |
|---|---|---|---|
| 1 | 指令的类型字段 `kind` 和 `StartAgentCommand.Kind` 撞名，所有消息都无法序列化 | 自动测试 | 指令类型字段改为 `op` |
| 2 | `herdr pane run` 成功时没有输出，被当成"返回的不是 JSON"，导致 reclaude 启动失败 | 本机实测 | 只关心成败的命令不再要求 JSON 输出；模拟 herdr 改成和真实一致 |
| 3 | `/read` 只取最后 N 行，结果全是 Claude 界面的空白和输入框 | 本机实测 | 先去掉输入框、合并空行，再取最后 N 行 |
| 4 | 管家卡在确认框，用户 30 分钟后才 `/yes`，初始说明再也不会发出，而且没有任何提示 | 本机实测 | 初始说明挂起，用户确认后立即发送；后台等待延长到 6 小时；超时会通知 |
| 5 | 服务器关闭要等约 11 秒（在等节点的长连接结束） | 本机实测 | 关闭时主动断开所有节点，降到 0.1 秒 |
| 6 | 设备断开时写"最后在线时间"失败会抛出未处理异常，**导致整个进程崩溃** | 自动测试偶发少跑，查结果文件发现测试进程崩溃 | 写失败只记日志；用"同名目录占住状态文件"的方式加了回归测试 |
| 7 | 推回的回复里带着大片空行和 Claude 右上角的"Update installed"提示 | 本机实测 | 合并空行，过滤靠右显示的界面提示 |
| 8 | 服务器按工作目录找 `appsettings.json`，开机自启时会读不到 | 部署检查 | 改为从程序所在目录读取 |
| 9 | 节点配置文件里多写了一个计算字段 | 本机实测 | 不再序列化 |
| 10 | 改成"先提交、再单独等待"后，刚确认完"信任此目录"时发出的初始说明会被 Claude 吞掉（herdr 已报告空闲，但界面还在初始化） | 审查修复后的本机复测 | 初始说明在就绪后等 3 秒再发，并确认 agent 开始工作，没动静就重发，最多 3 次；用户自己的指令不自动重发 |
| 11 | .NET 10 的后台服务在线程池上启动，微信通道登记到通知列表的时机晚于服务器启动，启动瞬间的通知可能丢失 | 自动测试偶发失败 | 通道改为在应用启动配置阶段登记 |
| 12 | 协议的"必填参数"校验把可空参数也当成必填，而序列化又省略了 null 字段，导致自己发的消息自己解析不了 | 自动测试 | 序列化时显式写出 null，保留严格校验（缺字段的消息仍然被拒绝） |

关于"测试数量偶尔变少"：最初看到的 177 / 183 个，实际是第 6 条导致测试进程崩溃、后面的测试没跑；我当时过滤输出时漏掉了"Test Run Aborted"这一行。修复后连续多轮都是完整的 212 个。

## 七、代码审查

审查方式：另起一个独立的审查者，只给它代码、需求文档和威胁模型，不给我的实现思路，要求逐条给出"文件:行号、严重程度、具体失败场景、修复建议"，并对其中 4 条用小程序实际复现。

审查结论：核心安全设计成立（令牌先验后升级、哈希加定长比较、证书指纹、参数数组、白名单），默认配置下**没有高危问题**。中低风险问题全部处理如下：

| # | 严重程度 | 问题 | 处理 | 回归测试 |
|---|---|---|---|---|
| 1 | 中 | 主人的消息并发处理、可能乱序（先 `/use` 后发的话可能发到原来的设备） | 微信消息进有序队列由单个任务处理；指令分发严格串行 | `Messages_in_one_batch_are_handled_strictly_in_order` |
| 2 | 中 | 整理回复或读屏幕出错时，"做完 / 等确认"的通知被悄悄吞掉 | 失败时改发提示文字，事件一定发出；herdr 和会话记录的 JSON 字段都先检查类型 | `Completion_is_reported_even_when_reading_the_reply_fails`、`Blocked_is_reported…`、`Unexpected_json_shapes_become_bad_response` |
| 3 | 中（Windows） | herdr 是 `.cmd` 包装脚本时参数经过 cmd.exe，可能被注入 | 配置校验拒绝 `.cmd/.bat/.ps1`；Windows 上只在 PATH 里找 `.exe` | `Script_wrappers_are_rejected_as_herdr_path` |
| 4 | 中低 | `/yes` 选第一个 Yes，可能是"自动接受修改"这种扩大权限的选项 | 优先纯 Yes，排除扩大权限的选项；只剩这类选项时列出来让用户用 `/keys` 自己选 | `Never_picks_an_option_that_escalates_permissions`、`Plain_yes_is_preferred` |
| 5 | 中低 | `/unpair` 写盘失败时撤销失效；握手期间撤销有竞态；同步游标写失败会丢一批消息 | 状态修改改为事务式，另设"尽力写入"；撤销一定在内存生效并断开；登记连接后复查设备是否还在 | `Unpair_takes_effect_even_when_saving_fails`、`Failed_writes_leave_memory_unchanged…` |
| 6 | 中低 | 回应确认框、发初始说明的后台等待没有数量上限，同一 agent 可能重复通知 | 所有后台跟踪计入上限，每个 agent 只有一个；挂起的初始说明一定会被发出或通知失败 | `One_watcher_per_agent_so_back_to_back_prompts_report_once` |
| 7 | 中低 | 没有死连接检测，电脑睡眠后要十几分钟才显示离线 | 服务器和节点都开启 20 秒心跳超时 | 真实验收 |
| 8 | 中低 | 目录白名单先访问文件系统再检查（Windows 会带凭据连 SMB，也会泄露目录是否存在） | 先做纯字符串检查，通过了才访问文件系统 | `Outside_paths_are_rejected_before_touching_the_disk` |
| 9 | 低 | 校验正则的 `$` 放过结尾换行（已复现） | 全部改为 `\z` | `Trailing_newline_does_not_bypass_validation` |
| 10 | 低 | 各处超时没对齐；启动时断线会留下半启动的工作区 | 每条指令设总时间预算（28 秒 / 启动 + 20 秒），收紧配置上限；启动失败无论什么原因都关掉工作区 | `Failed_start_closes_the_new_workspace` |
| 11 | 低 | 两个节点用同一个令牌会每秒互相顶替 | 被替换的节点识别出关闭原因后以退出码 0 退出 | `Replaced_node_exits_instead_of_flapping` |
| 12 | 低 | 不认识的指令会断开整个连接；结果缺 id 会抛异常；一台设备可以用通知刷屏（前两条已复现） | 不认识的指令单独回错误；协议严格校验必填字段；每台设备每分钟最多 20 条通知并按顺序排队 | `Unknown_commands_are_answered_individually…`、`Device_events_are_rate_limited_per_minute`、协议拒绝缺 id 的测试 |
| 13 | 低 | 大段空格会产生空消息并丢掉后面的内容（已复现） | 跳过空段 | `Whitespace_runs_never_produce_empty_chunks_or_lose_content` |
| 14 | 低 | 放在反向代理后面时限流变成全局的，测试通道的"只接受本机"检查失效 | WebSocket 只统计令牌错误的请求；在安全说明里写明代理的限制 | `Repeated_bad_tokens_are_throttled…` |
| 15 | 低 | 后台模式把微信登录链接写进日志，看得到日志的人扫码就成了主人 | 日志只写二维码图片路径（权限 600）；字符画只在交互式登录或真实终端显示 | `Login_link_is_never_written_to_logs` |
| 16 | 低 | 配对码先用掉再校验配置；部分异常没被捕获；reclaude 绝对路径含空格会出错 | 先校验再配对；扩大异常捕获；reclaude 保持用命令名 | `Pairing_validates_the_config_before_spending_the_one_time_code` |
| 17 | 低 | `--config ~/node.json` 会把主目录改成 700；写盘没有落盘；相对数据目录按工作目录解析 | 只给新建的目录设 700；改名前落盘；相对路径按程序目录解析 | `Existing_directories_keep_their_permissions` |
| 18 | 低 | 发给 agent 的文字作为 `--wait` 进程的参数挂几个小时，同机其他用户能用 `ps` 看到 | 先提交（几秒结束），再用状态变化序号判断开始干活，单独等待结果 | `Session_option_is_added_and_prompt_does_not_keep_text…`；真实验收中 Claude 干活期间进程列表里只有 `agent wait …`，没有指令原文 |

存疑项的处理：

| 存疑项 | 结论 |
|---|---|
| 进程退出后读输出没有超时 | 成立，读输出也纳入同一个截止时间 |
| `/ls` 序号对应的 pane 被别的程序占用 | herdr 不复用 pane ID；另外改为优先用 agent 名字作为目标（agent 退出后名字会被清除，不会发错对象） |
| 发指令前没确认 pane 里真的是 agent | 补上检查：没识别出 agent 类型的 pane 不发送 |
| 用 `/keys` 确认后不再跟踪 | 成立，`/keys` 里带确认类按键时和 `/yes` 一样继续跟踪 |
