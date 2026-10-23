using ChatPilot.Server.Chat;
using ChatPilot.Server.Devices;
using ChatPilot.Server.Options;
using ChatPilot.Server.State;
using ChatPilot.Server.WeChat;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace ChatPilot.Tests.Server;

public class ChatCommandParserTests
{
    [Theory]
    [InlineData("/help")]
    [InlineData("/")]
    [InlineData("／帮助")]
    public void Help(string text) => Assert.IsType<ChatHelp>(ChatCommandParser.Parse(text));

    [Fact]
    public void Prompt_keeps_body_verbatim_including_newlines()
    {
        var command = Assert.IsType<ChatPrompt>(ChatCommandParser.Parse("@mac/tideq 第一行\n第二行 $(id)"));
        Assert.Equal(new TargetRef("mac", "tideq"), command.Target);
        Assert.Equal("第一行\n第二行 $(id)", command.Text);
    }

    [Fact]
    public void Full_width_at_sign_and_index_targets()
    {
        var command = Assert.IsType<ChatPrompt>(ChatCommandParser.Parse("＠2 跑测试"));
        Assert.True(command.Target.IsIndex);
    }

    [Theory]
    [InlineData("@tideq")]
    [InlineData("@ 跑测试")]
    [InlineData("@a/b/c 跑测试")]
    [InlineData("@bad!dev/x 跑")]
    public void Malformed_prompts_return_usage(string text) => Assert.IsType<ChatInvalid>(ChatCommandParser.Parse(text));

    [Fact]
    public void Plain_text_goes_to_butler() =>
        Assert.Equal("看看哪个 agent 闲着", Assert.IsType<ChatButlerPrompt>(ChatCommandParser.Parse("  看看哪个 agent 闲着 ")).Text);

    [Fact]
    public void Oversized_text_is_rejected() =>
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("@x " + new string('a', ProtocolInfo.MaxPromptChars + 1)));

    [Theory]
    [InlineData("/new ~/code/web", null, "~/code/web", null, null)]
    [InlineData("/new win D:\\code\\web codex", "win", "D:\\code\\web", "codex", null)]
    [InlineData("/new mac /Users/me/x reclaude myx", "mac", "/Users/me/x", "reclaude", "myx")]
    [InlineData("/new \"/Users/me/My Project\"", null, "/Users/me/My Project", null, null)]
    [InlineData("/new “/Users/me/中文 目录” Claude", null, "/Users/me/中文 目录", "claude", null)]
    public void New_parses_device_path_kind_and_name(string text, string? device, string directory, string? kind, string? name)
    {
        var command = Assert.IsType<ChatNew>(ChatCommandParser.Parse(text));
        Assert.Equal((device, directory, kind, name), (command.Device, command.Directory, command.Kind, command.Name));
    }

    [Theory]
    [InlineData("/new")]
    [InlineData("/new relative/path")]
    [InlineData("/new mac")]
    [InlineData("/new ~/x claude Bad-Name")]
    [InlineData("/new \"/unterminated")]
    public void New_rejects_bad_input(string text) => Assert.IsType<ChatInvalid>(ChatCommandParser.Parse(text));

    [Fact]
    public void Read_validates_line_count()
    {
        Assert.Equal(60, Assert.IsType<ChatRead>(ChatCommandParser.Parse("/read tideq")).Lines);
        Assert.Equal(200, Assert.IsType<ChatRead>(ChatCommandParser.Parse("/read tideq 200")).Lines);
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/read tideq 0"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/read tideq 501"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/read tideq abc"));
    }

    [Fact]
    public void Keys_are_lowercased_and_whitelisted()
    {
        Assert.Equal(["down", "enter"], Assert.IsType<ChatKeys>(ChatCommandParser.Parse("/keys tideq Down ENTER")).Keys);
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/keys tideq ctrl+d"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/keys tideq"));
    }

    [Fact]
    public void Respond_rename_pair_unpair_use()
    {
        Assert.True(Assert.IsType<ChatRespond>(ChatCommandParser.Parse("/yes 1")).Approve);
        Assert.False(Assert.IsType<ChatRespond>(ChatCommandParser.Parse("/no mac/tideq")).Approve);
        Assert.Equal("web", Assert.IsType<ChatRename>(ChatCommandParser.Parse("/name 1 web")).Name);
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/name 1 Web"));
        Assert.Equal("公司电脑", Assert.IsType<ChatPair>(ChatCommandParser.Parse("/pair 公司电脑")).DeviceName);
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/pair a/b"));
        Assert.Equal("win", Assert.IsType<ChatUnpair>(ChatCommandParser.Parse("/unpair win")).Device);
        Assert.Equal("win", Assert.IsType<ChatUse>(ChatCommandParser.Parse("/use win")).Device);
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/use"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("/frobnicate"));
        Assert.IsType<ChatInvalid>(ChatCommandParser.Parse("   "));
    }
}

public sealed class PairingServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly StateStore _store;
    private readonly PairingService _pairing;

    public PairingServiceTests()
    {
        _store = new StateStore(_temp.Path);
        _pairing = new PairingService(_store, _time, Microsoft.Extensions.Options.Options.Create(new ServerOptions()));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Codes_are_eight_unambiguous_characters()
    {
        var code = _pairing.Create(null);
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code.Display);
    }

    [Fact]
    public void Code_is_single_use_and_token_is_stored_only_as_hash()
    {
        var code = _pairing.Create(null);
        var paired = Assert.IsType<PairingService.Paired>(_pairing.Redeem(code.Display.ToLowerInvariant(), "mac", "macOS"));
        Assert.Equal(43, paired.Token.Length);
        Assert.IsType<PairingService.InvalidCode>(_pairing.Redeem(code.Code, "mac", "macOS"));

        // S-05
        var file = File.ReadAllText(_store.FilePath);
        Assert.DoesNotContain(paired.Token, file, StringComparison.Ordinal);
        Assert.Contains(StateStore.HashToken(paired.Token), file, StringComparison.Ordinal);
        Assert.Equal(paired.Device.Id, _store.FindDeviceByToken(paired.Token)?.Id);
        Assert.Null(_store.FindDeviceByToken(paired.Token + "x"));
    }

    [Fact]
    public void Codes_expire()
    {
        var code = _pairing.Create(null);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.IsType<PairingService.InvalidCode>(_pairing.Redeem(code.Code, "mac", "macOS"));
    }

    [Fact]
    public void Wrong_code_does_not_consume_valid_code()
    {
        var code = _pairing.Create(null);
        Assert.IsType<PairingService.InvalidCode>(_pairing.Redeem("AAAA-AAAA", "mac", "x"));
        Assert.IsType<PairingService.InvalidCode>(_pairing.Redeem("not a code", "mac", "x"));
        Assert.IsType<PairingService.Paired>(_pairing.Redeem(code.Code, "mac", "x"));
    }

    [Fact]
    public void Suggested_name_wins_and_duplicates_get_suffix()
    {
        var first = Assert.IsType<PairingService.Paired>(_pairing.Redeem(_pairing.Create("win").Code, "ignored", "x"));
        var second = Assert.IsType<PairingService.Paired>(_pairing.Redeem(_pairing.Create(null).Code, "WIN", "x"));
        Assert.Equal("win", first.Device.Name);
        Assert.Equal("WIN-2", second.Device.Name);
        Assert.IsType<PairingService.InvalidName>(_pairing.Redeem(_pairing.Create(null).Code, "bad name", "x"));
    }

    [Fact]
    public void State_file_is_private()
    {
        _pairing.Redeem(_pairing.Create(null).Code, "mac", "x");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.FilePath));
        }

        Assert.Single(new StateStore(_temp.Path).Devices());
    }
}

public class ServerOptionsTests
{
    [Fact]
    public void Defaults_are_valid() => Assert.Empty(new ServerOptions().Validate());

    [Theory]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.2", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    public void Without_tls_only_loopback_is_allowed(string address, bool ok)
    {
        // S-15
        var options = new ServerOptions { ListenAddress = address, Tls = new TlsOptions { Enabled = false } };
        Assert.Equal(ok, options.Validate().Count == 0);
    }

    [Fact]
    public void Dev_channel_requires_strong_token()
    {
        var options = new ServerOptions { DevChannel = new DevChannelOptions { Enabled = true, Token = "short" } };
        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData("api.weixin.qq.com", true)]
    [InlineData("ilinkai.weixin.qq.com", true)]
    [InlineData("qq.com", true)]
    [InlineData("evil.com", false)]
    [InlineData("qq.com.evil.com", false)]
    [InlineData("evilqq.com", false)]
    public void Only_tencent_hosts_are_trusted_for_redirects(string host, bool trusted) =>
        Assert.Equal(trusted, WeChatLogin.IsTrustedHost(host));

    [Fact]
    public void Client_version_is_encoded_like_the_plugin() =>
        Assert.Equal(0x0002040Au, ILinkClient.ClientVersion("2.4.10"));
}

public class ReplyFormatterTests
{
    [Fact]
    public void Blocked_event_tells_how_to_respond()
    {
        var text = ReplyFormatter.AgentEvent("mac", new AgentEvent(AgentEventKind.Blocked, new AgentSummary("w1:p1", "tideq", "claude", "blocked", null, null, null), "Do you want to proceed?"));
        Assert.Contains("/yes mac/tideq", text, StringComparison.Ordinal);
        Assert.Contains("/no mac/tideq", text, StringComparison.Ordinal);
    }
}
