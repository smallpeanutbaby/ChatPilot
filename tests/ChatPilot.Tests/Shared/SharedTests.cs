using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ChatPilot.Shared;
using ChatPilot.Shared.Protocol;
using ChatPilot.Shared.Security;
using ChatPilot.Shared.Text;
using ChatPilot.Tests.Support;

namespace ChatPilot.Tests.Shared;

public class ProtocolTests
{
    public static TheoryData<WireMessage> Messages => new()
    {
        new HelloMessage(1, "0.1.0", "macOS", "host"),
        new WelcomeMessage("d_1", "mac"),
        new CommandMessage("1", new ListAgentsCommand()),
        new CommandMessage("2", new PromptCommand("w1:p1", "跑测试; $(id)")),
        new CommandMessage("3", new ReadCommand("tideq", 60)),
        new CommandMessage("4", new StartAgentCommand("~/code", "reclaude", "web", null)),
        new CommandMessage("5", new SendKeysCommand("tideq", ["down", "enter"])),
        new CommandMessage("6", new RespondCommand("tideq", true)),
        new CommandMessage("7", new RenameAgentCommand("w1:p1", "web")),
        new ResultMessage("1", CommandOutcome.Success(new AgentListData([new AgentSummary("w1:p1", "tideq", "claude", "idle", "TideQ", "/x", "标题")]))),
        new ResultMessage("2", CommandOutcome.Failure(ErrorCodes.NotFound, "没有")),
        new AgentEventMessage(new AgentEvent(AgentEventKind.Completed, new AgentSummary("w1:p1", null, "claude", "idle", null, null, null), "收到")),
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Messages_round_trip(WireMessage message)
    {
        var bytes = ProtocolJson.SerializeToUtf8(message);
        Assert.True(ProtocolJson.TryDeserialize(bytes, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(ProtocolJson.Serialize(message), ProtocolJson.Serialize(parsed));
    }

    [Fact]
    public void Wire_format_uses_snake_case_and_type_discriminator()
    {
        var json = ProtocolJson.Serialize(new CommandMessage("1", new StartAgentCommand("/x", "claude", null, null)));
        Assert.Contains("\"type\":\"command\"", json, StringComparison.Ordinal);
        Assert.Contains("\"op\":\"start_agent\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("initial_prompt", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"command","id":"1","command":{"op":"shell","cmd":"rm -rf /"}}""")]
    [InlineData("""{"type":"exec","cmd":"id"}""")]
    [InlineData("""{"id":"1"}""")]
    [InlineData("not json")]
    [InlineData("""{"type":"command","id":"1","command":{"op":"prompt","target":"x","text":"y"},"x":""" + "[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]" + "}")]
    public void Unknown_or_malformed_messages_are_rejected(string json)
    {
        // S-09：协议外的指令类型、缺类型、非 JSON、嵌套过深都解析失败，不会被执行。
        Assert.False(ProtocolJson.TryDeserialize(Encoding.UTF8.GetBytes(json), out _));
    }
}

public class ValidationTests
{
    [Theory]
    [InlineData("tideq", true)]
    [InlineData("a-b_c9", true)]
    [InlineData("Tideq", false)]
    [InlineData("9abc", false)]
    [InlineData("-rf", false)]
    [InlineData("a b", false)]
    [InlineData("abcdefghijabcdefghijabcdefghijabc", false)]
    public void Agent_names_follow_herdr_rules(string name, bool valid) => Assert.Equal(valid, Validation.IsAgentName(name));

    [Theory]
    [InlineData("w1:p2", true)]
    [InlineData("w12:p345", true)]
    [InlineData("w1:p", false)]
    [InlineData("w1:p2;id", false)]
    public void Pane_ids_are_strict(string id, bool valid) => Assert.Equal(valid, Validation.IsPaneId(id));

    [Theory]
    [InlineData("mac", true)]
    [InlineData("公司电脑", true)]
    [InlineData("win-10_a", true)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("-mac", false)]
    public void Device_names(string name, bool valid) => Assert.Equal(valid, Validation.IsDeviceName(name));

    [Theory]
    [InlineData("enter", true)]
    [InlineData("down", true)]
    [InlineData("ctrl+c", true)]
    [InlineData("3", true)]
    [InlineData("ctrl+d", false)]
    [InlineData("!", false)]
    [InlineData("a", false)]
    [InlineData("alt+x", false)]
    public void Only_whitelisted_keys_are_allowed(string key, bool allowed) => Assert.Equal(allowed, Validation.IsAllowedKey(key));
}

public class TextChunkerTests
{
    [Fact]
    public void Short_text_is_one_chunk() => Assert.Equal(["你好"], TextChunker.Split("你好", 100, 4));

    [Fact]
    public void Long_text_is_split_at_newlines_and_capped_at_max_chunks()
    {
        var text = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"第 {i} 行内容"));
        var chunks = TextChunker.Split(text, 4000, 4);
        Assert.Equal(4, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 4000));
        Assert.EndsWith(TextChunker.TruncatedNotice, chunks[^1], StringComparison.Ordinal);
        Assert.StartsWith("第 0 行", chunks[0], StringComparison.Ordinal);
        Assert.StartsWith("第 ", chunks[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Emoji_are_never_split()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 300));
        var chunks = TextChunker.Split(text, 101, 10);
        Assert.All(chunks, c => Assert.Equal(0, c.Length % 2));
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void Text_exactly_at_limit_is_not_truncated()
    {
        var text = new string('x', 8000);
        var chunks = TextChunker.Split(text, 4000, 2);
        Assert.Equal(text, string.Concat(chunks));
    }
}

public class CertificatePinTests
{
    [Fact]
    public void Matches_only_the_same_certificate()
    {
        using var a = CreateCertificate();
        using var b = CreateCertificate();
        var pin = CertificatePin.Compute(a);
        Assert.StartsWith("sha256:", pin, StringComparison.Ordinal);
        Assert.True(CertificatePin.Matches(a, pin));
        Assert.True(CertificatePin.Matches(a, pin.ToUpperInvariant().Replace("SHA256:", "", StringComparison.Ordinal)));
        Assert.False(CertificatePin.Matches(b, pin));
        Assert.False(CertificatePin.Matches(a, null));
        Assert.False(CertificatePin.Matches(a, "sha256:1234"));
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}

public class SecureFileTests
{
    [Fact]
    public void Files_are_written_atomically_with_owner_only_permissions()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("sub", "secret.json");
        SecureFile.WriteAllText(path, "one");
        SecureFile.WriteAllText(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(temp.Combine("sub")));
        if (!OperatingSystem.IsWindows())
        {
            // S-14
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(temp.Combine("sub")));
        }
    }
}
