using System.Text.RegularExpressions;
using ChatPilot.Server.Options;
using ChatPilot.Server.State;
using ChatPilot.Shared.Security;
using Microsoft.Extensions.Options;
using QRCoder;

namespace ChatPilot.Server.WeChat;

/// <summary>
/// 微信扫码绑定：取二维码 → 终端显示（同时保存 PNG）→ 轮询扫码状态 → 保存 bot token 和主人的微信号。
/// 处理二维码过期刷新（最多 3 次）、IDC 重定向、手机上显示验证码需要输入等情况。
/// </summary>
public sealed partial class WeChatLogin(
    ILinkClient client,
    StateStore store,
    IOptions<ServerOptions> options,
    TimeProvider time,
    ILogger<WeChatLogin> logger)
{
    private const int MaxQrAttempts = 3;
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(8);

    /// <summary>
    /// 显示二维码的方式，参数依次是：字符画、PNG 路径、登录链接（只在交互式登录时提供）。默认打印到终端，测试里可以替换。
    /// </summary>
    public Action<string, string, string?> ShowQrCode { get; set; } = PrintToConsole;

    /// <summary>
    /// 运行一次登录流程，成功返回 true。<paramref name="askVerifyCode"/> 为空时（后台服务模式）遇到验证码就放弃，
    /// 提示用 <c>chatpilot-server login</c> 交互登录。
    /// </summary>
    public async Task<bool> RunAsync(Func<string, Task<string?>>? askVerifyCode, CancellationToken cancellationToken)
    {
        var existingToken = store.WeChatSnapshot()?.BotToken;
        IReadOnlyList<string> localTokens = existingToken is null ? [] : [existingToken];

        var qr = await client.GetBotQrCodeAsync(localTokens, cancellationToken).ConfigureAwait(false);
        if (qr.Qrcode is null || qr.QrcodeImgContent is null)
        {
            logger.LogError("获取微信登录二维码失败：返回内容不完整");
            return false;
        }

        Show(qr.QrcodeImgContent, interactive: askVerifyCode is not null);
        var baseUrl = options.Value.WeChat.BaseUrl;
        var attempts = 1;
        string? verifyCode = null;
        var scannedLogged = false;
        var deadline = time.GetUtcNow() + LoginTimeout;

        while (time.GetUtcNow() < deadline)
        {
            var status = await client.GetQrCodeStatusAsync(baseUrl, qr.Qrcode, verifyCode, cancellationToken).ConfigureAwait(false);
            switch (status.Status)
            {
                case "wait":
                    break;

                case "scaned":
                    verifyCode = null;
                    if (!scannedLogged)
                    {
                        logger.LogInformation("已扫码，请在手机上确认");
                        scannedLogged = true;
                    }

                    break;

                case "need_verifycode":
                    if (askVerifyCode is null)
                    {
                        logger.LogError("需要输入手机上显示的数字。请先停止服务，再运行 chatpilot-server login 交互登录");
                        return false;
                    }

                    var prompt = verifyCode is null ? "输入手机微信上显示的数字：" : "数字不匹配，请重新输入：";
                    verifyCode = (await askVerifyCode(prompt).ConfigureAwait(false))?.Trim();
                    continue;

                case "expired":
                case "verify_code_blocked":
                    if (++attempts > MaxQrAttempts)
                    {
                        logger.LogError("二维码多次失效，登录已停止，请稍后重试");
                        return false;
                    }

                    logger.LogInformation("二维码已失效，正在刷新（{Attempt}/{Max}）", attempts, MaxQrAttempts);
                    verifyCode = null;
                    scannedLogged = false;
                    qr = await client.GetBotQrCodeAsync(localTokens, cancellationToken).ConfigureAwait(false);
                    if (qr.Qrcode is null || qr.QrcodeImgContent is null)
                    {
                        logger.LogError("刷新二维码失败");
                        return false;
                    }

                    Show(qr.QrcodeImgContent, interactive: askVerifyCode is not null);
                    break;

                case "binded_redirect":
                    if (existingToken is not null)
                    {
                        logger.LogInformation("这个微信已经绑定过本服务器，继续使用现有凭据");
                        return true;
                    }

                    logger.LogError("微信提示已经绑定过，但本地没有凭据。请在微信里解除 ClawBot 绑定后重试");
                    return false;

                case "scaned_but_redirect":
                    if (status.RedirectHost is { } host && IsTrustedHost(host))
                    {
                        baseUrl = "https://" + host;
                        logger.LogInformation("切换到 {Host} 继续轮询", host);
                    }
                    else
                    {
                        logger.LogWarning("忽略不可信的重定向地址");
                    }

                    break;

                case "confirmed":
                    return Save(status);

                default:
                    logger.LogDebug("未知的扫码状态 {Status}", status.Status);
                    break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
        }

        logger.LogError("等待扫码超时");
        return false;
    }

    private bool Save(QrStatusResponse status)
    {
        if (string.IsNullOrWhiteSpace(status.IlinkBotId) || string.IsNullOrWhiteSpace(status.BotToken) || string.IsNullOrWhiteSpace(status.IlinkUserId))
        {
            logger.LogError("登录确认了，但服务器没有返回完整的凭据");
            return false;
        }

        var baseUrl = options.Value.WeChat.BaseUrl;
        if (status.Baseurl is { } returned && Uri.TryCreate(returned, UriKind.Absolute, out var uri) && uri.Scheme == "https" && IsTrustedHost(uri.Host))
        {
            baseUrl = returned;
        }

        var previousOwner = store.Read(s => s.WeChat?.OwnerUserId);
        store.Update(state => state.WeChat = new WeChatAccount
        {
            BotId = status.IlinkBotId,
            BotToken = status.BotToken,
            BaseUrl = baseUrl,
            OwnerUserId = status.IlinkUserId,
            BoundAt = time.GetUtcNow(),
        });

        if (previousOwner is not null && previousOwner != status.IlinkUserId)
        {
            logger.LogWarning("绑定的微信号变了，之后只接受新微信号的指令");
        }

        logger.LogInformation("✅ 微信绑定成功");
        return true;
    }

    /// <summary>
    /// 展示二维码。二维码等于"成为主人"的凭证，所以登录链接绝不写进日志：
    /// 图片保存为权限 600 的 PNG；终端字符画只在交互式登录、或者输出是真实终端（不是日志文件）时显示。
    /// </summary>
    private void Show(string content, bool interactive)
    {
        var png = Path.Combine(options.Value.ResolvedDataDir, "wechat-login.png");
        var ascii = "";
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.L);
            SecureFile.WriteAllBytes(png, new PngByteQRCode(data).GetGraphic(8));
            ascii = new AsciiQRCode(data).GetGraphicSmall();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("保存二维码图片失败：{Message}", ex.Message);
        }

        if (interactive || !Console.IsOutputRedirected)
        {
            ShowQrCode(ascii, png, interactive ? content : null);
        }

        logger.LogInformation("微信登录二维码已保存到 {Path}（只有当前用户能读）。也可以停掉服务后运行 chatpilot-server login 在终端里扫码", png);
    }

    private static void PrintToConsole(string ascii, string pngPath, string? link)
    {
        Console.WriteLine();
        Console.WriteLine("用手机微信扫描下面的二维码，绑定 ChatPilot：");
        Console.WriteLine(ascii);
        Console.WriteLine($"（二维码图片也保存在 {pngPath}）");
        if (link is not null)
        {
            Console.WriteLine($"如果扫不了，用微信打开这个链接：{link}");
        }

        Console.WriteLine();
    }

    /// <summary>只接受腾讯的域名，防止被引导到其他服务器。</summary>
    internal static bool IsTrustedHost(string host) => TrustedHostRegex().IsMatch(host);

    [GeneratedRegex(@"^([a-z0-9-]+\.)*(qq|weixin|wechat)\.com\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrustedHostRegex();
}
