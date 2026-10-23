# 参与贡献

欢迎提 Issue 和 Pull Request。

## 报告问题

请附上：

- 系统和版本（服务器、节点分别在什么系统上），herdr 版本（`herdr --version`），出问题的 agent 类型和版本；
- 你在微信里发了什么、收到了什么；
- 节点日志和服务器日志里相关的几行（Windows 在 `%LOCALAPPDATA%\ChatPilot\`，Linux 用 `journalctl -u chatpilot-server`）。

日志里不会写令牌、配对码和消息内容，但贴出来之前还是请自己看一遍。

**安全问题请不要公开提 Issue**，私下联系维护者，说明复现方式；威胁模型见 [`docs/SECURITY.md`](docs/SECURITY.md)。

## 提交代码

`main` 分支受保护，所有改动（包括维护者自己的）都通过 Pull Request 合并，不能直接推送或强推。先 fork 仓库，在自己的分支上改，提交前跑通：

```bash
dotnet build ChatPilot.sln          # 警告视为错误；src 下公开成员必须有 XML 文档注释
dotnet test ChatPilot.sln           # 单元测试 + 集成测试 + 安全测试
```

- 改动节点的指令执行（`CommandExecutor`、`PathPolicy`、`HerdrClient`）时，记住节点是安全边界：服务器发来的每个参数都要校验，只能执行协议里定义的几种 herdr 操作。
- 新增对某个 agent 的支持（会话记录格式、终端界面、确认框），请附上真实样例做测试，并在 [`docs/AGENTS.md`](docs/AGENTS.md) 里写明验证情况。
- 注释用中文 XML 文档注释（`/// <summary>`），句末带句号；方法内部只在不显而易见的地方写注释，说明"为什么"。术语统一用"服务器""节点""设备""agent""主人"。

提交 Pull Request 即表示你同意以本项目的 [MIT 许可证](LICENSE) 授权你的贡献。
