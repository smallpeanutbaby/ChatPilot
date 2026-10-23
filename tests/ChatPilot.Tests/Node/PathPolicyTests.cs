using ChatPilot.Node.Execution;
using ChatPilot.Tests.Support;

namespace ChatPilot.Tests.Node;

/// <summary>S-07：目录白名单。</summary>
public sealed class PathPolicyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _allowed;
    private readonly string _outside;
    private readonly PathPolicy _policy;

    public PathPolicyTests()
    {
        _allowed = _temp.CreateDirectory("allowed");
        _temp.CreateDirectory("allowed", "project");
        _outside = _temp.CreateDirectory("outside");
        _policy = new PathPolicy([_allowed]);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Allows_root_and_subdirectories()
    {
        Assert.True(_policy.TryResolve(_allowed, out var root, out _));
        Assert.Equal(_allowed, root);
        Assert.True(_policy.TryResolve(Path.Combine(_allowed, "project"), out var sub, out _));
        Assert.Equal(Path.Combine(_allowed, "project"), sub);
        Assert.Equal(_allowed, _policy.DefaultDirectory);
    }

    [Fact]
    public void Rejects_directories_outside_the_whitelist()
    {
        Assert.False(_policy.TryResolve(_outside, out _, out var error));
        Assert.Contains("不在允许的目录内", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_dot_dot_traversal()
    {
        var sneaky = Path.Combine(_allowed, "project", "..", "..", "outside");
        Assert.False(_policy.TryResolve(sneaky, out _, out _));
    }

    [Fact]
    public void Rejects_sibling_with_common_prefix()
    {
        var sibling = _temp.CreateDirectory("allowed-evil");
        Assert.False(_policy.TryResolve(sibling, out _, out _));
    }

    [Fact]
    public void Rejects_symlinks_that_escape()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(_allowed, "escape");
        Directory.CreateSymbolicLink(link, _outside);
        Assert.False(_policy.TryResolve(link, out _, out _));
        Assert.False(_policy.TryResolve(Path.Combine(link, "."), out _, out _));
    }

    [Fact]
    public void Allows_symlinks_that_stay_inside()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(_allowed, "alias");
        Directory.CreateSymbolicLink(link, Path.Combine(_allowed, "project"));
        Assert.True(_policy.TryResolve(link, out var resolved, out _));
        Assert.Equal(Path.Combine(_allowed, "project"), resolved);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("project")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_relative_and_empty_paths(string input) => Assert.False(_policy.TryResolve(input, out _, out _));

    [Fact]
    public void Rejects_missing_directories_and_nul_bytes()
    {
        Assert.False(_policy.TryResolve(Path.Combine(_allowed, "missing"), out _, out var missing));
        Assert.Equal("目录不存在", missing);
        Assert.False(_policy.TryResolve(_allowed + "\0/x", out _, out _));
    }

    [Fact]
    public void Expands_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, PathPolicy.ExpandHome("~"));
        Assert.Equal(Path.Combine(home, "code"), PathPolicy.ExpandHome("~/code"));
        Assert.Equal("/abs", PathPolicy.ExpandHome("/abs"));
    }

    [Fact]
    public void No_roots_means_nothing_is_allowed()
    {
        var empty = new PathPolicy([]);
        Assert.False(empty.TryResolve(_allowed, out _, out var error));
        Assert.Contains("没有配置允许的目录", error, StringComparison.Ordinal);
        Assert.Null(empty.DefaultDirectory);
    }
}
