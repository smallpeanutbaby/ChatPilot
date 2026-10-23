using ChatPilot.Node.Connection;
using ChatPilot.Server;
using ChatPilot.Server.Options;
using ChatPilot.Server.Security;
using ChatPilot.Shared.Protocol;
using ChatPilot.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatPilot.Tests.Integration;

/// <summary>S-06：在真实的 Kestrel + 自签证书上验证证书指纹校验。</summary>
public sealed class TlsPinningTests : IAsyncLifetime
{
    private readonly TempDirectory _data = new();
    private WebApplication? _app;

    private WebApplication App => _app ?? throw new InvalidOperationException("服务器没有启动");

    private Uri BaseUri { get; set; } = new("https://127.0.0.1");

    private string Fingerprint => App.Services.GetRequiredService<CertificateProvider>().Fingerprint;

    public async Task InitializeAsync()
    {
        _app = ServerApp.Build(
        [
            "--environment=Testing",
            "--ChatPilot:DataDir=" + _data.Path,
            "--ChatPilot:ListenAddress=127.0.0.1",
            "--ChatPilot:Port=0",
            "--ChatPilot:WeChat:Enabled=false",
        ]);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        BaseUri = new Uri(address);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _data.Dispose();
    }

    [Fact]
    public async Task Correct_fingerprint_reaches_the_server()
    {
        Assert.Equal("https", BaseUri.Scheme);
        using var handler = PinnedTransport.CreateHttpHandler(Fingerprint);
        var ex = await Assert.ThrowsAsync<PairingException>(() =>
            PairingClient.PairAsync(BaseUri, handler, new PairRequest("AAAA-AAAA", "mac", "x"), CancellationToken.None));
        Assert.Contains("配对码无效", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_fingerprint_is_refused_for_pairing_and_websocket()
    {
        var wrong = "sha256:" + new string('0', 64);
        using var handler = PinnedTransport.CreateHttpHandler(wrong);
        var ex = await Assert.ThrowsAsync<PairingException>(() =>
            PairingClient.PairAsync(BaseUri, handler, new PairRequest("AAAA-AAAA", "mac", "x"), CancellationToken.None));
        Assert.Contains("连不上服务器", ex.Message, StringComparison.Ordinal);

        var connector = new ClientWebSocketConnector();
        var wsUri = ServerEndpoint.WebSocketUri(BaseUri);
        var wsError = await Assert.ThrowsAnyAsync<Exception>(() => connector.ConnectAsync(wsUri, "token", wrong, CancellationToken.None));
        Assert.IsNotType<NodeUnauthorizedException>(wsError);

        // 指纹正确但令牌不对：TLS 通过，然后被服务器以 401 拒绝。
        await Assert.ThrowsAsync<NodeUnauthorizedException>(() => connector.ConnectAsync(wsUri, "token", Fingerprint, CancellationToken.None));
    }

    [Fact]
    public void Certificate_is_persisted_and_reused()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { DataDir = _data.Path });
        using var reloaded = new CertificateProvider(options, NullLogger<CertificateProvider>.Instance);
        Assert.Equal(Fingerprint, reloaded.Fingerprint);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_data.Path, "server.pfx")));
        }
    }
}
