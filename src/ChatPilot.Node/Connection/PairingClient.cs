using System.Net;
using System.Net.Http.Json;
using ChatPilot.Shared.Protocol;

namespace ChatPilot.Node.Connection;

public sealed class PairingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>用配对码向服务器换设备令牌。</summary>
public static class PairingClient
{
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
