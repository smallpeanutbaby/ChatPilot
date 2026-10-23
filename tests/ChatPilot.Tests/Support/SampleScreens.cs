namespace ChatPilot.Tests.Support;

/// <summary>从真实 Claude Code 终端（herdr agent read）采集的样例。</summary>
internal static class SampleScreens
{
    public const string ClaudeReply = """
         ▐▛███▛█   Claude Code v2.1.308
        ▝▜██████▀  Opus 5.5 with xhigh effort · Claude Max
         ▝▝   ▝▝   /…/scratchpad/ws2

        ❯ 只回复两个字：收到。不要使用任何工具。

        ⏺ 收到

        ✻ Cogitated for 2s · done 11:26 AM

        ───────────────────────────────────────────────────────────────────────────────
        ❯
        ───────────────────────────────────────────────────────────────────────────────
          ⏵⏵ auto mode on (shift+tab to cycle)
        """;

    public const string ClaudeReplyWithTools = """
        ❯ 帮我改一下窗口尺寸

        ⏺ 我先看看现在的代码。

        ⏺ Read(src/MainWindow.cs)
          ⎿  Read 36 lines

        ⏺ Update(src/MainWindow.cs)
          ⎿  Updated src/MainWindow.cs with 12 additions
              16 +        FitToScreen();

        ⏺ 窗口尺寸改好了：
          - 占可用区域的 88%
          - 限制在 1280×800 ~ 1600×1000 之间

          需要我再加图标吗？

        ✻ Worked for 1m 41s · done 11:40 AM

        ───────────────────────────────────────────────────────────────────────────────
        ❯ Try "how do I log an error?"
        ───────────────────────────────────────────────────────────────────────────────
          ⏵⏵ auto mode on (shift+tab to cycle)
        """;

    /// <summary>"信任此目录"确认框：默认选中的是 No, exit。</summary>
    public const string TrustDialog = """
        me@MacBook-Pro ws2 % claude
        ───────────────────────────────────────────────────────────────────────────────
         Accessing workspace:
         /private/tmp/scratchpad/ws2
         Quick safety check: Is this a project you created or one you trust? (Like your own code, a well-known open source
         project, or work from your team). If not, take a moment to review what's in this folder first.
         Claude Code'll be able to read, edit, and execute files here.
         Security guide
         ❯ No, exit
           Yes, I trust this folder
         Enter to confirm · Esc to cancel
        """;

    /// <summary>工具权限确认框：默认选中的就是 Yes，带一行折行。</summary>
    public const string PermissionDialog = """
        ⏺ Bash(rm -rf build)

        ╭──────────────────────────────────────────────────────────────╮
        │ Bash command                                                 │
        │                                                              │
        │   rm -rf build                                               │
        │   Remove build output                                        │
        │                                                              │
        │ Do you want to proceed?                                      │
        │ ❯ 1. Yes                                                     │
        │   2. Yes, and don't ask again for rm commands in             │
        │      /Users/me/project                                       │
        │   3. No, and tell Claude what to do differently (esc)        │
        ╰──────────────────────────────────────────────────────────────╯
        """;

    /// <summary>空闲状态：只有输入框里的 ❯，不是确认框。</summary>
    public const string IdleScreen = """
         ▐▛███▛█   Claude Code v2.1.308

        ───────────────────────────────────────────────────────────────────────────────
        ❯ Try "how do I log an error?"
        ───────────────────────────────────────────────────────────────────────────────
          ⏵⏵ auto mode on (shift+tab to cycle) · ← for agents
        """;

    /// <summary>
    /// Codex 做完一轮（版式取自真实 Codex 终端，内容已替换）：用户输入是第 0 列的 ›，回复和工具调用都是 • 开头，
    /// 工具调用带 └ 输出、diff 行或折叠提示；回复后面是 Worked for 和 Recap，最底下是没有横线的输入行和状态栏。
    /// </summary>
    public const string CodexReply = """
        › 修改
            + 115 lines (ctrl+t to expand)

        • Added src\Orders.Tests\OrderTests.cs (+214 -0)
              1 +using System;
              2 +using System.Linq;
            + Show details

        • Ran dotnet test tests/Orders.Tests --no-restore
          └ 已通过! - 失败:     0，通过:    29，总计:    29
            + 5 lines (ctrl+t to expand)

        • 回归测试已得到预期结果，接下来补上聚合校验。

        • Edited src\Orders\Order.cs (+108 -17)
            241 -        if (!Lines.Any(existing => existing.Id == line.Id))
            241 +        var exists = Lines.Any(existing => existing.Id == line.Id);
            + Show details

        • Explored
          └ Read Order.cs

        • 已修改：

          • 校验器移到 Domain/Validators，删除接口，调用方直接使用具体
            类。

          全量 752 项测试通过；构建 0 错误。

          Worked for 12m 21s • 10:06

          ↳ Recap: 校验器迁移完成，全量测试通过，
                   最新调整尚未推送。



        › Ask Codex to do anything

          GPT-6.1-Sol xhigh · D:\code\orders · 订单校验
          ? for shortcuts
        """;

    /// <summary>
    /// pi 做完一轮（版式取自真实 pi 终端，内容已替换）：没有输入 / 回复标记，正文整体缩进一格，
    /// 输入区夹在两条横线之间，下面是状态栏。
    /// </summary>
    public const string PiReply = """
         上一个问题的回答


         帮我跑一下测试


         read tests/OrderTests.cs


         测试全部通过：
         - 29 项通过
         - 0 项失败

        ──────────────────────────────────────────────────────────────────────────────

        ──────────────────────────────────────────────────────────────────────────────
         pi-lens  !2W
        D:\code\orders (dev)
        ↑6.2M ↓456k $72.357 76.5%/1.0M (auto)                                  glm-5.3 • max
        """;
}
