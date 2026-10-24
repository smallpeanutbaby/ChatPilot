using ChatPilot.Node.Execution;
using ChatPilot.Tests.Support;

namespace ChatPilot.Tests.Node;

/// <summary>从终端提取最后一条回复（F-15、F-17），样例来自真实的 Claude Code / Codex / pi 终端。</summary>
public class ReplyExtractorTests
{
    [Fact]
    public void Extracts_simple_reply_from_real_screen() =>
        Assert.Equal("收到", ReplyExtractor.FromTerminal(SampleScreens.ClaudeReply, 1000));

    [Fact]
    public void Extracts_last_text_block_after_tool_calls()
    {
        var reply = ReplyExtractor.FromTerminal(SampleScreens.ClaudeReplyWithTools, 1000);
        Assert.Equal("窗口尺寸改好了：\n- 占可用区域的 88%\n- 限制在 1280×800 ~ 1600×1000 之间\n\n需要我再加图标吗？", reply);
    }

    [Fact]
    public void Blank_padding_and_right_aligned_notices_are_removed()
    {
        // 真实终端：回复后面有大片空行，输入框上方还有靠右的"Update installed"提示。
        var screen = "❯  --help; $(whoami) id\n\n⏺ 收到符号\n  第二段\n\n\n\n  第三段\n" + string.Concat(Enumerable.Repeat("\n", 18))
            + new string(' ', 78) + "✔ Update installed · Restart to apply\n"
            + new string('─', 80) + "\n❯\n" + new string('─', 80) + "\n  ⏵⏵ auto mode on";
        Assert.Equal("收到符号\n第二段\n\n第三段", ReplyExtractor.FromTerminal(screen, 1000));
    }

    [Fact]
    public void Falls_back_to_last_lines_when_structure_is_unknown()
    {
        var screen = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var reply = ReplyExtractor.FromTerminal(screen, 10000);
        Assert.StartsWith("line 21", reply, StringComparison.Ordinal);
        Assert.EndsWith("line 50", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_replies_keep_the_tail()
    {
        var reply = ReplyExtractor.FromTerminal("❯ q\n\n⏺ " + new string('a', 500) + "END", 100);
        Assert.Equal(100, reply.Length);
        Assert.EndsWith("END", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Recent_content_skips_input_box_and_blank_padding()
    {
        var screen = "第一行\n\n\n\n⏺ 第二行\n" + string.Concat(Enumerable.Repeat("\n", 40)) + SampleScreens.IdleScreen;
        Assert.Equal("第一行\n\n⏺ 第二行\n\n ▐▛███▛█   Claude Code v2.1.308", ReplyExtractor.RecentContent(screen, 10));
        Assert.Equal("⏺ 第二行\n\n ▐▛███▛█   Claude Code v2.1.308", ReplyExtractor.RecentContent(screen, 3));
    }

    [Fact]
    public void Screen_excerpt_drops_input_box()
    {
        var excerpt = ReplyExtractor.ScreenExcerpt(SampleScreens.ClaudeReply);
        Assert.DoesNotContain("auto mode", excerpt, StringComparison.Ordinal);
        Assert.EndsWith("✻ Cogitated for 2s · done 11:26 AM", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Extracts_codex_reply_and_skips_tool_blocks_and_turn_footer()
    {
        var reply = ReplyExtractor.FromTerminal(SampleScreens.CodexReply, 1000);
        Assert.Equal("已修改：\n\n• 校验器移到 Domain/Validators，删除接口，调用方直接使用具体\n  类。\n\n全量 752 项测试通过；构建 0 错误。", reply);
    }

    [Fact]
    public void Codex_input_line_and_status_bar_are_cut_from_recent_content()
    {
        var recent = ReplyExtractor.RecentContent(SampleScreens.CodexReply, 3);
        Assert.DoesNotContain("Ask Codex", recent, StringComparison.Ordinal);
        Assert.DoesNotContain("for shortcuts", recent, StringComparison.Ordinal);
        Assert.EndsWith("最新调整尚未推送。", recent, StringComparison.Ordinal);
    }

    [Fact]
    public void Codex_option_list_is_not_mistaken_for_the_input_line()
    {
        // 确认框里的 › 1. Yes 和输入行一样在第 0 列，不能被当成输入框截掉，否则推给用户的屏幕摘要里看不到选项。
        const string screen = """
        • Ran rm -rf build

          Would you like to run the following command?

          $ rm -rf build

        › 1. Yes, proceed (y)
          2. Yes, and don't ask again for this command (a)
          3. No, and tell Codex what to do differently (esc)

          Press enter to confirm or esc to cancel
        """;
        Assert.Contains("3. No, and tell Codex", ReplyExtractor.ScreenExcerpt(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void Agents_without_markers_are_anchored_on_the_echoed_prompt()
    {
        // pi 没有输入 / 回复标记：用回显的提交文字定位这一轮，去掉统一缩进和底部输入区。
        var reply = ReplyExtractor.FromTerminal(SampleScreens.PiReply, 1000, "帮我跑一下测试");
        Assert.Equal("read tests/OrderTests.cs\n\n测试全部通过：\n- 29 项通过\n- 0 项失败", reply);
        Assert.DoesNotContain("pi-lens", ReplyExtractor.ScreenExcerpt(SampleScreens.PiReply), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("帮我跑一下测试", true)]
    [InlineData("帮 我 跑 一下\n测试", true)]
    [InlineData("列出当前目录的文件", false)]
    [InlineData("继续", true)]
    [InlineData("", true)]
    public void Prompt_is_considered_visible_unless_surely_absent(string prompt, bool expected) =>
        Assert.Equal(expected, ReplyExtractor.MayShowPrompt(SampleScreens.PiReply, prompt));

    [Fact]
    public void Blocked_gemini_screen_keeps_the_dialog_below_the_echoed_prompt_box()
    {
        // 审查发现：确认框挡住输入框时，上方回显指令的框被当成输入框，确认框被截掉，推送的摘要是空的。
        const string screen = """
        ╭──────────────────────────────╮
        │ > 删掉 build 目录             │
        ╰──────────────────────────────╯
        ╭──────────────────────────────╮
        │ ?  Shell rm -rf build         │
        │ Allow execution?              │
        │ ● 1. Yes, allow once          │
        │   2. Yes, allow always        │
        │   3. No, suggest changes      │
        ╰──────────────────────────────╯
        """;
        Assert.Contains("3. No, suggest changes", ReplyExtractor.ScreenExcerpt(screen), StringComparison.Ordinal);
        Assert.Contains("Allow execution?", ReplyExtractor.RecentContent(screen, 20), StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_anchored_on_a_boxed_prompt_has_no_border_line()
    {
        const string screen = """
        ╭──────────────────────────────╮
        │ > 列出当前目录                │
        ╰──────────────────────────────╯
        ✦ 这里有 3 个文件。
        ╭──────────────────────────────╮
        │ >   Type your message        │
        ╰──────────────────────────────╯
        """;
        Assert.Equal("✦ 这里有 3 个文件。", ReplyExtractor.FromTerminal(screen, 1000, "列出当前目录"));
    }

    [Fact]
    public void Invisible_characters_do_not_hide_an_echoed_prompt() =>
        Assert.True(ReplyExtractor.MayShowPrompt("❯ ❤ 请把这个函数改成异步", "❤️ 请把这个函数改成异步"));

    [Fact]
    public void Wrapped_or_pasted_prompts_count_as_visible()
    {
        // 折行、缩进、边框都不影响；长文字被折叠成 [Pasted text …] 时判断不了，按"看得到"处理。
        Assert.True(ReplyExtractor.MayShowPrompt("│ > 请把这个函数改成\n│   异步的写法 │", "请把这个函数改成异步的写法，然后跑测试"));
        Assert.True(ReplyExtractor.MayShowPrompt("❯ [Pasted text #1 +20 lines]", "一段很长的需求说明……"));
        Assert.False(ReplyExtractor.MayShowPrompt(SampleScreens.IdleScreen, "一段很长的需求说明……"));
    }

    [Fact]
    public void Multi_line_prompts_are_anchored_on_their_last_line()
    {
        const string screen = """
        │ > 第一行要求
        │   最后一行要求
        ✦ 好的，已经处理完了。
        ╭──────────────────────────────╮
        │ >   Type your message        │
        ╰──────────────────────────────╯
        """;
        Assert.Equal("✦ 好的，已经处理完了。", ReplyExtractor.FromTerminal(screen, 1000, "第一行要求\n最后一行要求\n"));
    }
}

/// <summary>确认框导航（F-16）：认识各家 agent 的选中标记，选中不扩大权限的"同意"，不把输入框当成确认框。</summary>
public class ApprovalNavigatorTests
{
    [Fact]
    public void Trust_dialog_moves_down_to_yes_instead_of_pressing_enter_on_no()
    {
        Assert.True(ApprovalNavigator.TryPlanApprove(SampleScreens.TrustDialog, out var plan, out _));
        Assert.NotNull(plan);
        Assert.Equal(["down", "enter"], plan.Keys);
        Assert.Equal("Yes, I trust this folder", plan.OptionText);
    }

    [Fact]
    public void Permission_dialog_with_box_and_wrapped_option_just_presses_enter()
    {
        Assert.True(ApprovalNavigator.TryPlanApprove(SampleScreens.PermissionDialog, out var plan, out _));
        Assert.NotNull(plan);
        Assert.Equal(["enter"], plan.Keys);
        Assert.Equal("1. Yes", plan.OptionText);
    }

    [Fact]
    public void Cursor_below_yes_moves_up()
    {
        const string screen = """
         Do you want to proceed?
           1. Yes
           2. Yes, allow all edits
         ❯ 3. No
        """;
        Assert.True(ApprovalNavigator.TryPlanApprove(screen, out var plan, out _));
        Assert.Equal(["up", "up", "enter"], plan?.Keys);
    }

    [Fact]
    public void Never_picks_an_option_that_escalates_permissions()
    {
        // 审查 #4：退出计划模式时第一个 Yes 是"自动接受修改"，应该选"手动确认"那个。
        const string planMode = """
         Would you like to proceed?
         ❯ 1. Yes, and auto-accept edits
           2. Yes, and manually approve edits
           3. No, keep planning
        """;
        Assert.True(ApprovalNavigator.TryPlanApprove(planMode, out var plan, out _));
        Assert.Equal(["down", "enter"], plan?.Keys);
        Assert.Equal("2. Yes, and manually approve edits", plan?.OptionText);

        const string onlyEscalating = """
         Allow this tool?
         ❯ 1. Yes, and don't ask again
           2. Yes, allow all edits during this session
           3. No
        """;
        Assert.False(ApprovalNavigator.TryPlanApprove(onlyEscalating, out _, out var error));
        Assert.Contains("扩大权限", error, StringComparison.Ordinal);
        Assert.Contains("\n1. Yes, and don't ask again\n", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "1. Yes", "2. Yes, and don't ask again for rm", "3. No" }, 0)]
    [InlineData(new[] { "No, exit", "Yes, I trust this folder" }, 1)]
    [InlineData(new[] { "Yes, always", "Yes" }, 1)]
    [InlineData(new[] { "允许，并且不再询问", "允许" }, 1)]
    [InlineData(new[] { "Yes, skip permissions", "No" }, -1)]
    public void Plain_yes_is_preferred(string[] options, int expected) =>
        Assert.Equal(expected, ApprovalNavigator.PickApproveOption(options));

    [Theory]
    [InlineData(new[] { "1. Allow once", "2. Allow for this session", "3. Always allow", "4. Deny" }, 0)]
    [InlineData(new[] { "Approve for this session", "Approve once" }, 1)]
    [InlineData(new[] { "Yes, allow always", "Yes, allow once" }, 1)]
    [InlineData(new[] { "Always allow", "Allow", "Deny" }, 1)]
    [InlineData(new[] { "Auto-run everything (shift+tab)", "Run (once) (y)", "Skip (esc or n)" }, 1)]
    [InlineData(new[] { "Trust, always allow in this session", "Yes, single permission", "No (tab to edit)" }, 1)]
    [InlineData(new[] { "Yes, proceed (y)", "Yes, and don't ask again for this command (a)" }, 0)]
    public void Once_options_beat_session_wide_ones(string[] options, int expected) =>
        Assert.Equal(expected, ApprovalNavigator.PickApproveOption(options));

    [Fact]
    public void Codex_cursor_is_recognised_and_composer_is_skipped()
    {
        const string screen = """
          Would you like to run the following command?

          $ rm -rf build

        › 1. Yes, proceed (y)
          2. Yes, and don't ask again for this command (a)
          3. No, and tell Codex what to do differently (esc)

          Press enter to confirm or esc to cancel
        """;
        Assert.True(ApprovalNavigator.TryPlanApprove(screen, out var plan, out _));
        Assert.Equal(["enter"], plan?.Keys);
        Assert.Equal("1. Yes, proceed (y)", plan?.OptionText);

        Assert.False(ApprovalNavigator.TryPlanApprove(SampleScreens.CodexReply, out _, out var error));
        Assert.Contains("没有找到确认框", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cursor_agent_arrow_picks_run_once()
    {
        const string screen = """
          Run this command?
          rm -rf build
          → Run (once) (y)
            Add Shell(rm) to allowlist? (tab)
            Auto-run everything (shift+tab)
            Skip (esc or n)
        """;
        Assert.True(ApprovalNavigator.TryPlanApprove(screen, out var plan, out _));
        Assert.Equal(["enter"], plan?.Keys);
        Assert.Equal("Run (once) (y)", plan?.OptionText);
    }

    [Fact]
    public void Kimi_triangle_cursor_moves_off_the_session_wide_option()
    {
        const string screen = """
          Run this command?
            1. Approve once
          ▶ 2. Approve for this session
            3. Reject
          ↵ confirm  1/2/3 choose
        """;
        Assert.True(ApprovalNavigator.TryPlanApprove(screen, out var plan, out _));
        Assert.Equal(["up", "enter"], plan?.Keys);
        Assert.Equal("1. Approve once", plan?.OptionText);
    }

    [Fact]
    public void Gemini_boxed_radio_dialog_is_understood()
    {
        const string screen = """
        ╭──────────────────────────────────────╮
        │ ?  Shell rm -rf build                 │
        │                                       │
        │ Allow execution?                      │
        │                                       │
        │ ● 1. Yes, allow once                  │
        │   2. Yes, allow always ...            │
        │   3. No, suggest changes (esc)        │
        ╰──────────────────────────────────────╯
        """;
        Assert.True(ApprovalNavigator.TryPlanApprove(screen, out var plan, out _));
        Assert.Equal(["enter"], plan?.Keys);
        Assert.Equal("1. Yes, allow once", plan?.OptionText);
    }

    [Fact]
    public void Grok_radio_buttons_are_understood()
    {
        const string screen = """
          Allow command: rm -rf build
          ┃ 1 (○) Yes, always allow rm
          ┃ 2 (●) No, reject
          ┃ 3 (○) Yes, proceed
          1/3:select · Ctrl+o:yolo · Ctrl+c:cancel
        """;
        Assert.True(ApprovalNavigator.TryPlanApprove(screen, out var plan, out _));
        Assert.Equal(["down", "enter"], plan?.Keys);
        Assert.Equal("3. Yes, proceed", plan?.OptionText);
    }

    [Fact]
    public void Droid_chevron_cursor_is_understood()
    {
        const string screen = """
          Execute command: rm -rf build
            No, cancel
          > Yes, allow
            Yes, always allow
          Use ↑↓ to navigate, Enter to select, Esc to cancel
        """;
        Assert.True(ApprovalNavigator.TryPlanApprove(screen, out var plan, out _));
        Assert.Equal(["enter"], plan?.Keys);
        Assert.Equal("Yes, allow", plan?.OptionText);
    }

    [Theory]
    [InlineData("  > Note:\n    1. Run the tests\n    2. Push")]
    [InlineData("❯ 请按顺序做：\n  1. 运行测试\n  2. 提交代码")]
    [InlineData("• 下一步建议：\n› 1. Run the tests\n  2. Push to dev")]
    public void Lists_without_a_decline_option_are_not_dialogs(string screen)
    {
        // 审查发现：agent 回复里的编号列表、回显的多行指令被当成确认框，/yes 会往里按回车。
        Assert.False(ApprovalNavigator.TryPlanApprove(screen, out _, out var error));
        Assert.Contains("没有找到确认框", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Yes, allow for this project")]
    [InlineData("Yes, and trust this project permanently")]
    [InlineData("Yes (globally)")]
    [InlineData("允许，以后都不用问")]
    [InlineData("Yes, and allow this host for this conversation")]
    public void Persistent_grants_are_never_picked(string grant) =>
        Assert.Equal(-1, ApprovalNavigator.PickApproveOption([grant, "No"]));

    [Fact]
    public void Idle_input_box_is_not_mistaken_for_a_dialog()
    {
        Assert.False(ApprovalNavigator.TryPlanApprove(SampleScreens.IdleScreen, out _, out var error));
        Assert.Contains("没有找到确认框", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Dialog_without_an_approve_option_is_refused()
    {
        const string screen = """
         Pick a model
         ❯ Opus
           Sonnet
        """;
        Assert.False(ApprovalNavigator.TryPlanApprove(screen, out _, out var error));
        Assert.Contains("/keys", error, StringComparison.Ordinal);
    }
}

/// <summary>由目录名生成 agent 名字和工作区标签。</summary>
public class AgentNamingTests
{
    [Theory]
    [InlineData("/Users/me/TideQ", "tideq")]
    [InlineData("/Users/me/My Project", "my-project")]
    [InlineData("/Users/me/项目", "agent")]
    [InlineData("/Users/me/2048", "a-2048")]
    [InlineData("/Users/me/web/", "web")]
    public void Derives_valid_names(string directory, string expected) =>
        Assert.Equal(expected, AgentNaming.FromDirectory(directory, new HashSet<string>()));

    [Fact]
    public void Avoids_existing_names_and_stays_within_32_chars()
    {
        Assert.Equal("web-3", AgentNaming.FromDirectory("/x/web", new HashSet<string> { "web", "web-2" }));
        var longName = AgentNaming.MakeUnique(new string('a', 32), new HashSet<string> { new('a', 32) });
        Assert.Equal(32, longName.Length);
        Assert.EndsWith("-2", longName, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_label_strips_control_chars_and_leading_dash() =>
        Assert.Equal("rf", AgentNaming.WorkspaceLabel("/x/--rf\u0007"));
}
