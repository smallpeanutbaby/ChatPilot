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
        huzitao@MacBook-Pro ws2 % claude
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
}
