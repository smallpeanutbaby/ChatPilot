using System.Net;
using System.Net.Http.Json;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Node.Connection;

/// <summary>配对失败，异常消息可以直接给用户看。</summary>
public sealed class PairingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>用配对码向服务器换设备令牌。</summary>
public static class PairingClient
{
    /// <summary>用配对码换设备令牌。</summary>
    /// <param name="server">服务器地址。</param>
    /// <param name="handler">HTTP 处理器（带证书指纹校验，见 <see cref="PinnedTransport.CreateHttpHandler"/>）。</param>
    /// <param name="request">配对请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="PairingException">配对码无效、请求太频繁或连不上服务器。</exception>
    public static async Task<PairResponse> PairAsync(Uri server, HttpMessageHandler handler, PairRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(ServerEndpoint.PairUri(server), request, ProtocolJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PairingException($"连不上服务器（如果是证书指纹不对，也会这样）：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PairingException("连接服务器超时", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await response.Content.ReadFromJsonAsync<PairResponse>(ProtocolJson.Options, cancellationToken).ConfigureAwait(false)
                        ?? throw new PairingException("服务器返回了空结果");
                case HttpStatusCode.Unauthorized:
                    throw new PairingException("配对码无效或已过期，请在微信里重新发 /pair");
                case HttpStatusCode.TooManyRequests:
                    throw new PairingException("尝试太频繁，请过一分钟再试");
                case HttpStatusCode.BadRequest:
                    var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(ProtocolJson.Options, cancellationToken).ConfigureAwait(false);
                    throw new PairingException("请求不合法：" + (error?.Error ?? "未知原因"));
                default:
                    throw new PairingException($"服务器返回 {(int)response.StatusCode}");
            }
        }
    }
}
