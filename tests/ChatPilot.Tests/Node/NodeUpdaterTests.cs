using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatPilot.Node.Cli;
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
    public void Versions_compare_like_releases(string candidate, string current, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.Parse(candidate)!.IsNewerThan(ReleaseVersion.Parse(current)!));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2")]
    [InlineData("1.2.3 ")]
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
        await updater.InstallAsync(latest, CancellationToken.None);

        Assert.Equal("new version", File.ReadAllText(exe));
        Assert.DoesNotContain(Directory.GetFiles(_install.Path), f => Path.GetFileName(f).StartsWith(".chatpilot-node.new", StringComparison.Ordinal));
        if (OperatingSystem.IsWindows())
        {
            // 正在运行的程序不能覆盖，旧的改名留着，下次启动时删掉。
            Assert.Equal("old version", File.ReadAllText(exe + ".old"));
            NodeUpdater.CleanUpOldBinaries(exe);
            Assert.False(File.Exists(exe + ".old"));
        }
        else
        {
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
        Assert.Single(Directory.GetFiles(_install.Path));
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
        Assert.Single(Directory.GetFiles(_install.Path));
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

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Only_a_service_running_this_exact_program_is_restarted(int exitCode, bool expected)
    {
        // Windows 计划任务的动作是 cmd.exe /c ""程序路径" run >> 日志"。
        var exe = Path.Combine(_install.Path, "chatpilot-node.exe");
        var output = new ProcessOutput(exitCode, $"cmd.exe /c \"\"{exe.ToUpperInvariant()}\" run >> \"log\" 2>&1\"");
        Assert.Equal(expected, ServiceRestarter.RunsExecutable(output, exe, ignoreCase: true));
        Assert.False(ServiceRestarter.RunsExecutable(output, Path.Combine(_install.Path, "other", "chatpilot-node.exe"), ignoreCase: true));
        Assert.False(ServiceRestarter.RunsExecutable(null, exe, ignoreCase: true));
    }

    private string Program(string rid, string contents)
    {
        var path = _install.Combine(NodeUpdater.ExecutableName(rid));
        File.WriteAllText(path, contents);
        return path;
    }

    private NodeUpdater Updater(string rid, string exe, bool runs = true) =>
        new(new HttpClient(_github), Repository, rid, exe, (_, _) => Task.FromResult(runs));

    /// <summary>模拟 GitHub 的版本接口和下载地址。</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

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
