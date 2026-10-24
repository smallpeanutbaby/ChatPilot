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

    /// <summary>设备断开多少秒还没连回来，就通知主人"离线了"（恢复时也会通知）；0 表示不通知。</summary>
    public int OfflineNoticeSeconds { get; set; } = 120;

    /// <summary>
    /// 主人所在的时区，例如 <c>Asia/Shanghai</c>：<c>/at 23:00</c> 按它计算，回复里的时间也按它显示。
    /// 不写时用服务器系统的时区（云服务器通常是 UTC）。
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>Telegram 通道设置（备用聊天通道，默认关闭）。</summary>
    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>数据目录的绝对路径。相对路径按程序所在目录解析（开机自启时工作目录通常是 /）。</summary>
    public string ResolvedDataDir =>
        string.IsNullOrWhiteSpace(DataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chatpilot", "server")
            : Path.GetFullPath(DataDir, AppContext.BaseDirectory);

    /// <summary>启动前检查配置；有问题直接拒绝启动。</summary>
    /// <returns>每个问题一条给管理员看的说明（包括 Telegram 设置的问题）；配置没问题时为空列表。</returns>
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

        if (OfflineNoticeSeconds is < 0 or > 86400)
        {
            errors.Add("OfflineNoticeSeconds 必须在 0–86400 之间（0 表示不通知）");
        }

        // 不认识的时区不拒绝启动（最小安装的 Linux 可能没有时区数据），启动时记一条警告、按系统时区算，见 TimeZoneWarning。
        if (WeChat.Enabled && (!Uri.TryCreate(WeChat.CdnBaseUrl, UriKind.Absolute, out var cdn) || cdn.Scheme != "https"))
        {
            errors.Add("WeChat:CdnBaseUrl 必须是 https:// 开头的地址");
        }

        errors.AddRange(Telegram.Validate());
        return errors;
    }

    /// <summary>主人的时区：配置了并且认识就用配置的，否则用系统时区。</summary>
    /// <returns>配置的时区；没配置或不认识时为 <see cref="TimeZoneInfo.Local"/>。</returns>
    public TimeZoneInfo ResolveTimeZone() =>
        !string.IsNullOrWhiteSpace(TimeZone) && TryFindTimeZone(TimeZone, out var zone) ? zone : TimeZoneInfo.Local;

    /// <summary>配置的时区不认识时给出的警告；没问题时为 null。</summary>
    /// <returns>写进启动日志的警告文字；没配置时区或时区认识时为 null。</returns>
    public string? TimeZoneWarning() =>
        !string.IsNullOrWhiteSpace(TimeZone) && !TryFindTimeZone(TimeZone, out _)
            ? $"不认识配置的时区 TimeZone={TimeZone}（拼写不对，或者系统没装时区数据，Debian/Ubuntu 上装 tzdata），/at 按系统时区 {TimeZoneInfo.Local.Id} 计算"
            : null;

    /// <summary>按 ID 找系统时区（去掉首尾空白），找不到或时区数据损坏时不抛异常。</summary>
    /// <param name="id">时区 ID，例如 <c>Asia/Shanghai</c>。</param>
    /// <param name="zone">找到的时区；找不到时为 <see cref="TimeZoneInfo.Utc"/>，不应使用。</param>
    /// <returns>找到时为 <c>true</c>。</returns>
    private static bool TryFindTimeZone(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}

/// <summary>
/// Telegram 通道设置：用 Telegram 机器人收发指令，作为微信的备用（iLink 不是公开接口，可能被调整）。
/// 只认 <see cref="OwnerUserId"/> 这一个 Telegram 用户。
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; }

    /// <summary>@BotFather 给的 bot token，例如 <c>123456:ABC…</c>。相当于密码，建议用环境变量 <c>ChatPilot__Telegram__BotToken</c> 配置。</summary>
    public string? BotToken { get; set; }

    /// <summary>主人的 Telegram 用户 ID（数字）。不知道的话先给机器人发一条消息，服务器日志里会打印发消息的人的 ID。</summary>
    public long? OwnerUserId { get; set; }

    /// <summary>Bot API 地址。</summary>
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    /// <summary>访问 Telegram 用的 HTTP 代理，例如 <c>http://127.0.0.1:7890</c>；不写时直连。</summary>
    public string? Proxy { get; set; }

    /// <summary>单条消息的最大字符数（Telegram 上限 4096）。</summary>
    public int MaxChunkChars { get; set; } = 4000;

    /// <summary>一次回复最多拆成几条消息。</summary>
    public int MaxChunks { get; set; } = 4;

    /// <summary>检查配置；没启用时不检查。</summary>
    /// <returns>每个问题一条给管理员看的说明；没启用或配置没问题时为空列表。</returns>
    public IReadOnlyList<string> Validate()
    {
        if (!Enabled)
        {
            return [];
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(BotToken) || BotToken.Length > 100 || !BotToken.Contains(':', StringComparison.Ordinal)
            || BotToken.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '-')))
        {
            errors.Add("开启 Telegram 时必须配置 Telegram:BotToken（@BotFather 给的 token，形如 123456:ABC…）");
        }

        if (OwnerUserId is not > 0)
        {
            errors.Add("开启 Telegram 时必须配置 Telegram:OwnerUserId（你的 Telegram 用户 ID，数字）");
        }

        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var api) || api.Scheme is not ("https" or "http"))
        {
            errors.Add("Telegram:ApiBaseUrl 不是合法的地址");
        }

        if (Proxy is not null && (!Uri.TryCreate(Proxy, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https" or "socks5")))
        {
            errors.Add("Telegram:Proxy 必须是 http://、https:// 或 socks5:// 开头的代理地址");
        }

        if (MaxChunkChars is < 200 or > 4096)
        {
            errors.Add("Telegram:MaxChunkChars 必须在 200–4096 之间");
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

    /// <summary>下载主人发来的图片用的微信 CDN 地址（和腾讯插件一致）。</summary>
    public string CdnBaseUrl { get; set; } = "https://novac2c.cdn.weixin.qq.com/c2c";

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
