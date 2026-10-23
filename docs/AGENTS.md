# 各类 agent 的支持细节

ChatPilot 通过 [herdr](https://herdr.dev) 操作 agent，所以 herdr 能管的 agent 都能用：发任务、`/read`、`/keys`、改名、做完通知都不分类型。
各类型的差别只在两处：推送的回复从哪里读，以及 `/yes` 能不能自动选中"同意"。

## 推送的回复

能找到会话记录时读记录，内容完整准确；找不到就读终端。读终端时，Claude Code 和 Codex 能认出回复块，其他 agent 取这一轮指令之后的全部输出（可能夹着工具调用的输出）。

读会话记录需要 herdr 上报会话：装好对应的 herdr 集成，例如 `herdr integration install codex`；`herdr integration status` 可以查看哪些已装、哪些过期。

| 类型 | 回复从哪里读 | 验证情况 |
|---|---|---|
| claude / reclaude | 会话记录：`~/.claude/projects`、`CLAUDE_CONFIG_DIR`、reclaude 各账号目录、节点配置的 `claudeProjectRoots` | macOS 真机验收；Windows 真机冒烟测试（`/new`、信任目录的 `/yes`、推送回复） |
| codex | 会话记录：`$CODEX_HOME`（默认 `~/.codex`）下的 `sessions`、`archived_sessions` | Windows 真机冒烟测试（`/new`、推送回复）；`/yes` 只有测试样例 |
| pi | 会话记录：herdr 直接给出路径 | Windows 真机冒烟测试；pi 默认没有确认框 |
| kimi | 会话记录：`$KIMI_CODE_HOME`（默认 `~/.kimi-code`）下的 `sessions` | 用真实会话文件核对 |
| qwen | 会话记录：`$QWEN_RUNTIME_DIR` / `$QWEN_HOME`（默认 `~/.qwen`）下的 `projects/*/chats`；settings 里自定义的 `advanced.runtimeOutputDir` 不支持，请用 `QWEN_RUNTIME_DIR` | 按 Qwen Code 0.26 发布包实现，未真机验证 |
| opencode / kilo | SQLite（只读打开）：`$XDG_DATA_HOME`（默认 `~/.local/share`，Windows 也一样）下的 `opencode/opencode.db`、`kilo/kilo.db`，可用 `OPENCODE_DB` / `KILO_DB` 指定 | 按 opencode 1.19 / kilo 7.8 源码实现，未真机验证 |
| droid | 会话记录：`$FACTORY_HOME_OVERRIDE`（默认用户主目录）下的 `.factory/sessions` | 按 Droid 0.248 发布包实现，未真机验证 |
| cursor | 会话记录：`$CURSOR_DATA_DIR`（默认 `~/.cursor`）下的 `projects/*/agent-transcripts`；Cursor 把思考过程接在回复正文后面写，无法拆开 | 按 Cursor Agent CLI 发布包实现，未真机验证 |
| copilot | 会话记录：`$COPILOT_HOME`（默认 `~/.copilot`）下的 `session-state` | 按 Copilot CLI 1.0.63 发布包实现，未真机验证 |
| grok | 会话记录：`$GROK_HOME`（默认 `~/.grok`）下的 `sessions/*/<id>/updates.jsonl`；有用户报告 Windows 上不写这个文件，那时退回读终端 | 按官方文档实现，可信度中等，未真机验证 |
| gemini | 终端（herdr 不上报它的会话） | 未真机验证 |
| 其他类型 | 终端 | 未真机验证 |

会话记录格式是各 agent 的内部格式，agent 升级后可能变化；读不了时自动退回读终端，不会报错。

## `/yes`

认得竖排选项列表里的选中标记：`❯`（Claude Code、Gemini、Qwen、Copilot）、`›`（Codex）、`→`（Cursor）、`▶`（Kimi）、`▸`（Hermes）、`>`（Droid）、`●`，以及 Grok 的单选框 `(●)`。

只做"这一次同意"：优先选纯粹的 Yes / Allow，其次是"仅这一次"（Allow once、Run (once)），再其次是其他同意类选项。"不再询问""总是允许""本会话 / 本工作区都允许""加入白名单""自动执行"这类会扩大权限的选项一律不自动选，只剩这类选项时会列出来，让你用 `/keys` 自己选。

横排按钮（opencode / kilo 的 Allow once · Allow always · Reject）在纯文本里看不出选中的是哪个，不自动处理，请用 `/keys`。

## 能启动的类型

`/new` 能启动 herdr 0.9.4 支持的全部类型：agy、amp、claude、cline、codex、copilot、cursor、devin、droid、gemini、grok、hermes、kilo、kimi、kiro、letta、maki、mastracode、muse、omp、opencode、pi、qodercli、qwen，再加上节点配置 `launchers` 里自定义的启动命令（默认带 reclaude：运行 `reclaude`，等 herdr 识别出 claude）。

**Windows 注意**：npm 装的 codex、kimi、pi 等是通过 `.ps1` 脚本启动的。PowerShell 的执行策略没设置时（默认禁止运行脚本），herdr 新开的终端里启动不了它们，`/new` 会报"timed out waiting for agent startup"。执行一次 `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` 即可。
