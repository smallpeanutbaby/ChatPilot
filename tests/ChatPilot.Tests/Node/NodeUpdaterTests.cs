using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPilot.Node.Cli;
using ChatPilot.Node.Herdr;
using ChatPilot.Tests.Support;

namespace ChatPilot.Tests.Node;

/// <summary><c>chatpilot-node update</c>：查版本、下载、核对、替换。</summary>
public sealed class NodeUpdaterTests : IDisposable
{
    private const string Repository = "owner/ChatPilot";
    private readonly TempDirectory _install = new();
    private readonly FakeGitHub _github = new();

    public void Dispose() => _install.Dispose();

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v0.1.1", "0.1.0+e9de875", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.2.0-beta.1", true)]
    [InlineData("0.2.0-beta.2", "0.2.0-beta.1", true)]
    [InlineData("0.2.0-beta.1", "0.2.0", false)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.2.0-beta.10", "0.2.0-beta.9", true)]
    [InlineData("0.2.0-beta.9", "0.2.0-beta.10", false)]
    [InlineData("0.2.0-rc.10", "0.2.0-rc.2", true)]
    [InlineData("0.2.0-beta.1", "0.2.0-beta", true)]
    [InlineData("0.2.0-beta", "0.2.0-beta.1", false)]
    [InlineData("0.2.0-alpha", "0.2.0-1", true)]
    [InlineData("0.2.0-rc.1", "0.2.0-beta.11", true)]
    [InlineData("0.2.0-beta.010", "0.2.0-beta.9", true)]
    [InlineData("0.2.0-beta.12345678901234567890", "0.2.0-beta.9", true)]
    public void Versions_compare_like_releases(string candidate, string current, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.Parse(candidate)!.IsNewerThan(ReleaseVersion.Parse(current)!));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2")]
    [InlineData("1.2.3 ")]
    [InlineData("v\u0661.0.0")]
    [InlineData("1.\u0662.3")]
    [InlineData("\uFF11.0.0")]
    public void Non_versions_are_rejected(string text) => Assert.Null(ReleaseVersion.Parse(text));

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task Latest_release_replaces_the_program(string rid)
    {
        var exe = Program(rid, "old version");
        _github.Publish("v0.2.0", rid, "new version");
        var updater = Updater(rid, exe);

        var latest = await updater.GetLatestAsync(CancellationToken.None);
        Assert.Equal("0.2.0", latest.Version.ToString());
        var replaced = await updater.InstallAsync(latest, CancellationToken.None);

        Assert.Equal("new version", File.ReadAllText(exe));
        Assert.DoesNotContain(InstallFiles(), f => Path.GetFileName(f).StartsWith(".chatpilot-node.new", StringComparison.Ordinal));
        if (OperatingSystem.IsWindows())
        {
            // 正在运行的程序不能覆盖，旧的改名留着，下次启动时删掉；改名后的路径交给重启那一步去找留下来的旧进程。
            Assert.Equal(exe + ".old", replaced);
            Assert.Equal("old version", File.ReadAllText(exe + ".old"));
            NodeUpdater.CleanUpOldBinaries(exe);
            Assert.False(File.Exists(exe + ".old"));
        }
        else
        {
            Assert.Null(replaced);
            Assert.True(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task Checksum_mismatch_leaves_the_program_untouched()
    {
        var exe = Program("win-x64", "old version");
        _github.Publish("v0.2.0", "win-x64", "new version", tamper: true);
        var updater = Updater("win-x64", exe);

        var error = await Assert.ThrowsAsync<UpdateException>(async () => await updater.InstallAsync(await updater.GetLatestAsync(CancellationToken.None), CancellationToken.None));
        Assert.Contains("校验值对不上", error.Message, StringComparison.Ordinal);
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.Single(InstallFiles());
    }

    [Fact]
    public async Task Program_that_does_not_run_here_is_not_installed()
    {
        var exe = Program("win-x64", "old version");
        _github.Publish("v0.2.0", "win-x64", "new version");
        var updater = Updater("win-x64", exe, runs: false);

        var error = await Assert.ThrowsAsync<UpdateException>(async () => await updater.InstallAsync(await updater.GetLatestAsync(CancellationToken.None), CancellationToken.None));
        Assert.Contains("运行不起来", error.Message, StringComparison.Ordinal);
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.Single(InstallFiles());
    }

    [Fact]
    public async Task Release_without_this_platform_or_checksums_is_refused()
    {
        var exe = Program("osx-arm64", "old version");
        _github.Publish("v0.2.0", "win-x64", "new version");
        var updater = Updater("osx-arm64", exe);
        var missing = await Assert.ThrowsAsync<UpdateException>(async () => await updater.InstallAsync(await updater.GetLatestAsync(CancellationToken.None), CancellationToken.None));
        Assert.Contains("没有这个平台", missing.Message, StringComparison.Ordinal);

        _github.Publish("v0.2.0", "osx-arm64", "new version", withChecksums: false);
        var unsigned = await Assert.ThrowsAsync<UpdateException>(async () => await updater.InstallAsync(await updater.GetLatestAsync(CancellationToken.None), CancellationToken.None));
        Assert.Contains("没有校验文件", unsigned.Message, StringComparison.Ordinal);
        Assert.Equal("old version", File.ReadAllText(exe));
    }

    [Fact]
    public async Task Repository_without_releases_says_so()
    {
        var updater = Updater("win-x64", Program("win-x64", "old"));
        var error = await Assert.ThrowsAsync<UpdateException>(() => updater.GetLatestAsync(CancellationToken.None));
        Assert.Contains("还没有发布过版本", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_switches_take_no_value()
    {
        // 真机测试发现：--check 被当成要带值的选项，update --check 直接报错。
        var cli = CommandLine.Parse(["update", "--check", "--no-restart"]);
        Assert.Empty(cli.Errors);
        Assert.True(cli.Has("check") && cli.Has("no-restart"));
    }

    [Fact]
    public void Checksum_file_is_parsed_like_sha256sum_output()
    {
        var hash = new string('a', 64);
        var sums = NodeUpdater.ParseChecksums($"{hash}  chatpilot-node-win-x64.zip\n{hash} *chatpilot-node-linux-x64.tar.gz\r\nnot a line\n");
        Assert.Equal(hash, sums["chatpilot-node-win-x64.zip"]);
        Assert.Equal(hash, sums["chatpilot-node-linux-x64.tar.gz"]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public async Task Stalled_download_gives_up_instead_of_hanging()
    {
        // 审查 #5：收到响应头之后 HttpClient.Timeout 就不管了，连接半断开时读内容会一直卡住。
        var exe = Program("win-x64", "old version");
        _github.Publish("v0.2.0", "win-x64", "new version");
        _github.Override(NodeUpdater.AssetName("win-x64"), () => new StreamContent(new SlowStream(firstChunk: 10, stallAfter: true)));
        var updater = Updater("win-x64", exe);

        var error = await Assert.ThrowsAsync<UpdateException>(async () => await updater.InstallAsync(await updater.GetLatestAsync(CancellationToken.None), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("卡住了", error.Message, StringComparison.Ordinal);
        Assert.Equal("old version", File.ReadAllText(exe));
    }

    [Fact]
    public async Task Trickling_download_is_cut_off_at_the_deadline()
    {
        // 每次都有一点数据（不算卡住），但总时间有上限。
        var exe = Program("win-x64", "old version");
        _github.Publish("v0.2.0", "win-x64", "new version");
        _github.Override(NodeUpdater.AssetName("win-x64"), () => new StreamContent(new SlowStream(firstChunk: 1, stallAfter: false)));
        var updater = new NodeUpdater(new HttpClient(_github), Repository, "win-x64", exe, (_, _) => Task.FromResult(true))
        {
            StallTimeout = TimeSpan.FromSeconds(5),
            BodyDeadline = TimeSpan.FromMilliseconds(500),
        };

        var error = await Assert.ThrowsAsync<UpdateException>(async () => await updater.InstallAsync(await updater.GetLatestAsync(CancellationToken.None), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("还没下完", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_info_that_stalls_or_breaks_is_reported_not_thrown_raw()
    {
        // 审查 #5 / #6：版本信息也是 ResponseHeadersRead 读的；读的过程中出错要变成给用户看的 UpdateException，不能让程序带着堆栈崩掉。
        var updater = Updater("win-x64", Program("win-x64", "old"));
        _github.Override("latest", () => new StreamContent(new SlowStream(firstChunk: 5, stallAfter: true)));
        var stalled = await Assert.ThrowsAsync<UpdateException>(() => updater.GetLatestAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("卡住了", stalled.Message, StringComparison.Ordinal);

        _github.Override("latest", () => new StreamContent(new BrokenStream()));
        var broken = await Assert.ThrowsAsync<UpdateException>(() => updater.GetLatestAsync(CancellationToken.None));
        Assert.Contains("中断了", broken.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(broken.InnerException);
    }

    [Fact]
    public async Task Concurrent_update_of_the_same_program_is_refused()
    {
        // 审查 #12：两个 update 同时运行会覆盖、删掉对方解出来的程序。
        var exe = Program("win-x64", "old version");
        _github.Publish("v0.2.0", "win-x64", "new version");
        var updater = Updater("win-x64", exe);
        var latest = await updater.GetLatestAsync(CancellationToken.None);
        using (ExclusiveFileLock.TryAcquire(NodeUpdater.UpdateLockPath(exe)))
        {
            var error = await Assert.ThrowsAsync<UpdateException>(() => updater.InstallAsync(latest, CancellationToken.None));
            Assert.Contains("另一个 chatpilot-node update", error.Message, StringComparison.Ordinal);
            Assert.Equal("old version", File.ReadAllText(exe));
        }

        await updater.InstallAsync(latest, CancellationToken.None);
        Assert.Equal("new version", File.ReadAllText(exe));
    }

    [Fact]
    public async Task Each_install_extracts_to_its_own_staging_file()
    {
        // 审查 #12：解出来的新程序每次用不同的名字，别人留下的旧式文件名也不会被删。
        var exe = Program("linux-x64", "old version");
        var foreign = _install.Combine(".chatpilot-node.new");
        File.WriteAllText(foreign, "someone else's");
        _github.Publish("v0.2.0", "linux-x64", "new version");
        var staged = new List<string>();
        var updater = new NodeUpdater(new HttpClient(_github), Repository, "linux-x64", exe, (path, _) =>
        {
            staged.Add(path);
            return Task.FromResult(true);
        });

        var latest = await updater.GetLatestAsync(CancellationToken.None);
        await updater.InstallAsync(latest, CancellationToken.None);
        await updater.InstallAsync(latest, CancellationToken.None);

        Assert.Equal(2, staged.Distinct(StringComparer.Ordinal).Count());
        Assert.All(staged, path => Assert.StartsWith(".chatpilot-node.new-", Path.GetFileName(path), StringComparison.Ordinal));
        Assert.Equal("someone else's", File.ReadAllText(foreign));
    }

    [Fact]
    public void Failed_rollback_keeps_the_original_error_and_says_where_the_old_program_is()
    {
        // 审查 #10：Windows 上换上新程序失败、改回旧程序也失败时，第二个错误不能盖掉第一个，还要告诉用户旧程序在哪。
        var exe = Program("win-x64", "old version");
        var staged = _install.Combine(".chatpilot-node.new-test.exe");
        File.WriteAllText(staged, "new version");
        var original = new IOException("新程序被杀毒软件锁住了");
        var error = Assert.Throws<UpdateException>(() => NodeUpdater.Replace(staged, exe, windows: true, (from, to, overwrite) =>
        {
            if (from == staged)
            {
                throw original;
            }

            if (to == exe)
            {
                throw new IOException("旧程序也被锁住了");
            }

            File.Move(from, to, overwrite);
        }));

        Assert.Same(original, error.InnerException);
        Assert.Contains("新程序被杀毒软件锁住了", error.Message, StringComparison.Ordinal);
        Assert.Contains("旧程序也被锁住了", error.Message, StringComparison.Ordinal);
        Assert.Contains(exe + ".old", error.Message, StringComparison.Ordinal);
        Assert.Equal("old version", File.ReadAllText(exe + ".old"));
    }

    [Fact]
    public void Successful_rollback_rethrows_the_original_error()
    {
        var exe = Program("win-x64", "old version");
        var staged = _install.Combine(".chatpilot-node.new-test.exe");
        File.WriteAllText(staged, "new version");
        var original = new UnauthorizedAccessException("没有权限");
        var error = Assert.Throws<UnauthorizedAccessException>(() => NodeUpdater.Replace(staged, exe, windows: true, (from, to, overwrite) =>
        {
            if (from == staged)
            {
                throw original;
            }

            File.Move(from, to, overwrite);
        }));

        Assert.Same(original, error);
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void Only_the_backups_the_updater_creates_are_cleaned_up()
    {
        // 审查 #8：用户自己留的备份（例如手动升级时留的 .old-mine）不能被删。
        var exe = Program("win-x64", "current");
        string[] ours = [".old", ".old1", ".old9"];
        string[] theirs = [".old10", ".old-mine", ".oldbackup", ".old.bak"];
        foreach (var suffix in ours.Concat(theirs))
        {
            File.WriteAllText(exe + suffix, suffix);
        }

        NodeUpdater.CleanUpOldBinaries(exe);

        Assert.All(ours, suffix => Assert.False(File.Exists(exe + suffix), suffix));
        Assert.All(theirs, suffix => Assert.True(File.Exists(exe + suffix), suffix));
        Assert.Equal("current", File.ReadAllText(exe));
    }

    [Fact]
    public void Cleanup_never_throws()
    {
        // 审查 #8：清理失败（目录不存在、路径奇怪）不能让节点启动失败。
        NodeUpdater.CleanUpOldBinaries(_install.Combine("missing", "chatpilot-node.exe"));
        NodeUpdater.CleanUpOldBinaries(_install.Path + Path.DirectorySeparatorChar);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Trial_run_uses_the_shared_process_runner(int exitCode, bool runs)
    {
        // 审查 #15：试运行和别处一样用 IProcessRunner（关掉标准输入、超时结束整个进程树）。
        var runner = new ScriptedRunner((_, _) => new ProcessResult(exitCode, "用法", ""));
        Assert.Equal(runs, await NodeUpdater.TryRunAsync(runner, "/opt/new/chatpilot-node", CancellationToken.None));
        var (file, args) = Assert.Single(runner.Calls);
        Assert.Equal("/opt/new/chatpilot-node", file);
        Assert.Equal(["help"], args);
    }

    [Fact]
    public async Task Trial_run_that_cannot_start_or_hangs_means_it_does_not_run()
    {
        Assert.False(await NodeUpdater.TryRunAsync(new ScriptedRunner((_, _) => throw new ProcessStartException("无法启动", new InvalidOperationException())), "x", CancellationToken.None));
        Assert.False(await NodeUpdater.TryRunAsync(new ScriptedRunner((_, _) => throw new TimeoutException()), "x", CancellationToken.None));
    }

    private string Program(string rid, string contents)
    {
        var path = _install.Combine(NodeUpdater.ExecutableName(rid));
        File.WriteAllText(path, contents);
        return path;
    }

    private NodeUpdater Updater(string rid, string exe, bool runs = true) =>
        new(new HttpClient(_github), Repository, rid, exe, (_, _) => Task.FromResult(runs)) { StallTimeout = TimeSpan.FromMilliseconds(300) };

    /// <summary>安装目录里的文件，不算升级锁文件（Linux / macOS 上它留在原地）。</summary>
    private IEnumerable<string> InstallFiles() =>
        Directory.GetFiles(_install.Path).Where(f => Path.GetFileName(f) != ".chatpilot-node.update.lock");

    /// <summary>按脚本回答的假进程启动器，记下每次调用。</summary>
    private sealed class ScriptedRunner(Func<string, IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(respond(fileName, arguments));
        }
    }

    /// <summary>先给几个字节，然后要么再也不给（卡住），要么每 50 毫秒给一个字节（很慢但没卡住）。</summary>
    private sealed class SlowStream(int firstChunk, bool stallAfter) : Stream
    {
        private int _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent < firstChunk)
            {
                var count = Math.Min(buffer.Length, firstChunk - _sent);
                buffer.Span[..count].Fill((byte)'x');
                _sent += count;
                return count;
            }

            await Task.Delay(stallAfter ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(50), cancellationToken);
            buffer.Span[0] = (byte)'x';
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>读的时候连接被重置。</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("连接被重置"));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new IOException("连接被重置"));

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("连接被重置");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>模拟 GitHub 的版本接口和下载地址。</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<HttpContent>> _overrides = new(StringComparer.Ordinal);

        /// <summary>让某个文件（或 <c>latest</c>）返回指定的内容，例如卡住的流。</summary>
        public void Override(string key, Func<HttpContent> content) => _overrides[key] = content;

        public void Publish(string tag, string rid, string executable, bool tamper = false, bool withChecksums = true)
        {
            _files.Clear();
            var asset = NodeUpdater.AssetName(rid);
            var archive = Archive(rid, Encoding.UTF8.GetBytes(executable));
            _files[asset] = archive;
            var assets = new List<object> { Asset(asset) };
            if (withChecksums)
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(tamper ? [1, 2, 3] : archive));
                _files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes($"{hash}  {asset}\n");
                assets.Add(Asset("SHA256SUMS.txt"));
            }

            _files["latest"] = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = tag, assets });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var key = path == $"/repos/{Repository}/releases/latest" ? "latest" : path.Split('/')[^1];
            if (_overrides.TryGetValue(key, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
            }

            return Task.FromResult(_files.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static object Asset(string name) => new { name, browser_download_url = $"https://github.com/{Repository}/releases/download/v0.2.0/{name}" };

        /// <summary>和发布流程打的包一样：顶层目录 chatpilot-node-平台/，里面是程序和自启脚本。</summary>
        private static byte[] Archive(string rid, byte[] executable)
        {
            var folder = $"chatpilot-node-{rid}/";
            using var buffer = new MemoryStream();
            if (rid.StartsWith("win-", StringComparison.Ordinal))
            {
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    Write(zip.CreateEntry(folder + "install-node-task.ps1"), "script"u8.ToArray());
                    Write(zip.CreateEntry(folder + "chatpilot-node.exe"), executable);
                }
            }
            else
            {
                using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
                using (var tar = new TarWriter(gzip))
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, folder));
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, folder + "chatpilot-node.service") { DataStream = new MemoryStream("unit"u8.ToArray()) });
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, folder + "chatpilot-node") { DataStream = new MemoryStream(executable) });
                }
            }

            return buffer.ToArray();
        }

        private static void Write(ZipArchiveEntry entry, byte[] data)
        {
            using var stream = entry.Open();
            stream.Write(data);
        }
    }
}
