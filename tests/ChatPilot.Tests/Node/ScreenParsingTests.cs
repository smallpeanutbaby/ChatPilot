using System.Text.Json;
using ChatPilot.Node.Execution;
using ChatPilot.Tests.Support;

namespace ChatPilot.Tests.Node;

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
    public void Extracts_assistant_text_after_last_human_prompt_from_transcript()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("session.jsonl");
        object[] entries =
        [
            new { type = "user", message = new { role = "user", content = "旧问题" } },
            new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "旧回答" } } } },
            new { type = "user", message = new { role = "user", content = new object[] { new { type = "text", text = "新问题" } } } },
            new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "我先看看" }, new { type = "tool_use", name = "Read" } } } },
            new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", content = "file" } } } },
            new { type = "assistant", isSidechain = true, message = new { role = "assistant", content = new object[] { new { type = "text", text = "子任务的话" } } } },
            new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "改好了" } } } },
        ];
        File.WriteAllLines(path, entries.Select(e => JsonSerializer.Serialize(e)).Append("{broken json"));

        Assert.Equal("我先看看\n\n改好了", ReplyExtractor.FromClaudeTranscript(path, 1000));
        Assert.Null(ReplyExtractor.FromClaudeTranscript(temp.Combine("missing.jsonl"), 1000));
    }
}

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
