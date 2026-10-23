using System.Net;

namespace ChatPilot.Server.Options;

/// <summary>服务器配置，对应 appsettings.json 的 <c>ChatPilot</c> 节，也可以用环境变量 <c>ChatPilot__Port</c> 这种形式覆盖。</summary>
public sealed class ServerOptions
{
    /// <summary>配置节的名字。</summary>
    public const string Section = "ChatPilot";

    /// <summary>数据目录（状态文件、证书、登录二维码）。默认 <c>~/.chatpilot/server</c>。</summary>
    public string? DataDir { get; set; }

    /// <summary>监听地址。不开 TLS 时必须是回环地址。</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>监听端口；0 表示随机分配（测试用）。</summary>
    public int Port { get; set; } = 8443;

    /// <summary>节点用来连接的公网地址，例如 <c>https://example.com:8443</c>，会写进 /pair 的提示里。</summary>
    public string? PublicUrl { get; set; }

    /// <summary>TLS 设置。</summary>
    public TlsOptions Tls { get; set; } = new();

    /// <summary>微信通道设置。</summary>
    public WeChatOptions WeChat { get; set; } = new();

    /// <summary>本机测试通道设置。</summary>
    public DevChannelOptions DevChannel { get; set; } = new();

    /// <summary>等节点返回结果的时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>等节点启动 agent 的时间（秒）。</summary>
    public int StartAgentTimeoutSeconds { get; set; } = 120;

    /// <summary>配对码有效期（分钟）。</summary>
    public int PairingCodeMinutes { get; set; } = 10;

    /// <summary>数据目录的绝对路径。相对路径按程序所在目录解析（开机自启时工作目录通常是 /）。</summary>
    public string ResolvedDataDir =>
        string.IsNullOrWhiteSpace(DataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chatpilot", "server")
            : Path.GetFullPath(DataDir, AppContext.BaseDirectory);

    /// <summary>启动前检查配置；有问题直接拒绝启动。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IPAddress.TryParse(ListenAddress, out var address))
        {
            errors.Add($"ListenAddress {ListenAddress} 不是合法的 IP 地址");
        }
        else if (!Tls.Enabled && !IPAddress.IsLoopback(address))
        {
            errors.Add("没开 TLS 时只能监听回环地址（127.0.0.1 或 ::1），否则设备令牌会明文传输");
        }

        if (Port is < 0 or > 65535)
        {
            errors.Add("Port 必须在 0–65535 之间");
        }

        if (PublicUrl is not null
            && (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out var publicUri) || publicUri.Scheme is not ("http" or "https")))
        {
            errors.Add("PublicUrl 必须是 http:// 或 https:// 开头的地址");
        }

        if (DevChannel.Enabled && (DevChannel.Token is null || DevChannel.Token.Length < 16))
        {
            errors.Add("开启测试通道时必须设置至少 16 位的 DevChannel:Token");
        }

        if (WeChat.Enabled && !Uri.TryCreate(WeChat.BaseUrl, UriKind.Absolute, out _))
        {
            errors.Add("WeChat:BaseUrl 不是合法的地址");
        }

        if (WeChat.MaxChunkChars is < 200 or > 4000)
        {
            errors.Add("WeChat:MaxChunkChars 必须在 200–4000 之间");
        }

        if (CommandTimeoutSeconds is < 5 or > 300 || StartAgentTimeoutSeconds is < 10 or > 600)
        {
            errors.Add("超时设置超出范围");
        }

        if (PairingCodeMinutes is < 1 or > 60)
        {
            errors.Add("PairingCodeMinutes 必须在 1–60 之间");
        }

        return errors;
    }
}

/// <summary>TLS 设置。</summary>
public sealed class TlsOptions
{
    /// <summary>使用自签证书（首次启动自动生成，节点靠指纹校验）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>微信通道设置。</summary>
public sealed class WeChatOptions
{
    /// <summary>是否启用微信通道。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>腾讯 iLink 接口地址。</summary>
    public string BaseUrl { get; set; } = "https://ilinkai.weixin.qq.com";

    /// <summary>iLink 的 bot 类型（插件固定使用 3）。</summary>
    public string BotType { get; set; } = "3";

    /// <summary>兼容的 iLink 渠道协议版本（对应腾讯开源的 openclaw-weixin 插件版本）。</summary>
    public string ChannelVersion { get; set; } = "2.4.10";

    /// <summary>自报身份，类似 User-Agent，只用于腾讯侧的日志统计。</summary>
    public string BotAgent { get; set; } = "ChatPilot/0.1.0";

    /// <summary>单条微信消息的最大字符数（腾讯插件用的是 4000）。</summary>
    public int MaxChunkChars { get; set; } = 4000;

    /// <summary>一次回复最多拆成几条消息。</summary>
    public int MaxChunks { get; set; } = 4;
}

/// <summary>本机测试通道设置。</summary>
public sealed class DevChannelOptions
{
    /// <summary>本机测试通道：只接受回环地址，必须带令牌。默认关闭。</summary>
    public bool Enabled { get; set; }

    /// <summary>测试通道令牌，至少 16 位；请求时放在 <c>X-ChatPilot-Dev-Token</c> 请求头里。</summary>
    public string? Token { get; set; }
}
