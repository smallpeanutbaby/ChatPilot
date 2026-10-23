using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChatPilot.Node.Execution;

/// <summary>
/// opencode 和它的分支 kilo 把会话存在 SQLite 里（<c>opencode.db</c> / <c>kilo.db</c>）。
/// 只读打开，取"最后一次真人输入之后，assistant 说的所有文字"。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>表结构（opencode 1.19、kilo 7.8）：<c>session(id, parent_id, revert)</c>、<c>message(id, session_id, time_created, data)</c>、
/// <c>part(id, message_id, data)</c>，data 列是 JSON。</item>
/// <item>真人输入：role 为 user，且至少有一个不是 synthetic / ignored 的 text 块（和 opencode 自己界面的判断一致）；
/// 子任务、shell 输出、压缩后的"继续"等注入的 user 消息都是 synthetic。</item>
/// <item>数据库开着 WAL，最新的内容在 -wal 文件里，所以必须通过 SQLite 读，不能复制文件。</item>
/// <item>用户撤销（revert）后，撤销点之后的消息不算。</item>
/// </list>
/// </remarks>
public static class OpenCodeStore
{
    /// <summary>沿 parent_id 往上找根会话最多走几层（kilo 可能上报子 agent 的会话）。</summary>
    private const int MaxParentDepth = 8;

    /// <summary>最多往回看多少条消息（一轮里每次工具调用都是一条 assistant 消息）。</summary>
    private const int MaxMessages = 500;

    /// <summary>库被 agent 锁住时每条语句最多等几秒。</summary>
    private const int BusyTimeoutSeconds = 3;

    /// <summary>这个数据库里有没有这个会话。打不开或不是 opencode 的库时返回 false。</summary>
    public static bool HasSession(string databasePath, string sessionId)
    {
        try
        {
            using var connection = Open(databasePath);
            return Scalar(connection, "SELECT 1 FROM session WHERE id = $id", sessionId) is not null;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return false;
        }
    }

    /// <summary>取最后一条回复；读不了或没有内容时返回 null。</summary>
    public static string? LastReply(string databasePath, string sessionId, int maxChars)
    {
        try
        {
            using var connection = Open(databasePath);
            var root = RootSession(connection, sessionId);
            var texts = CollectTurn(connection, root);
            if (texts.Count == 0)
            {
                return null;
            }

            var reply = string.Join("\n\n", texts).Trim();
            return reply.Length <= maxChars ? reply : "…" + reply[^(maxChars - 1)..];
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return null;
        }
    }

    /// <summary>库被锁、表结构不认识、data 列不是 JSON 等读取失败，都当作"读不到"，由调用方退回到读终端。</summary>
    private static bool IsReadFailure(Exception ex) => ex is SqliteException or JsonException or InvalidCastException or InvalidOperationException;

    private static SqliteConnection Open(string databasePath)
    {
        // 不进连接池：用完立刻释放文件句柄，不影响 agent 自己写库。
        // 库被锁时每条语句最多等 3 秒（Microsoft.Data.Sqlite 按命令超时自己重试，默认 30 秒，PRAGMA busy_timeout 管不住）。
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>上报的可能是子 agent 的会话，沿 parent_id 找到用户直接对话的根会话。</summary>
    private static string RootSession(SqliteConnection connection, string sessionId)
    {
        var current = sessionId;
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            if (Scalar(connection, "SELECT parent_id FROM session WHERE id = $id", current) is not string parent || parent.Length == 0)
            {
                break;
            }

            current = parent;
        }

        return current;
    }

    private static List<string> CollectTurn(SqliteConnection connection, string sessionId)
    {
        var (revertMessage, revertPart) = Revert(connection, sessionId);
        var reverted = revertMessage is not null;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, data FROM message WHERE session_id = $id ORDER BY time_created DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$limit", MaxMessages);
        var messages = new List<(string Id, string Data)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                messages.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var texts = new List<string>();
        foreach (var (id, data) in messages)
        {
            string? partLimit = null;
            if (reverted)
            {
                // 撤销点之后（含撤销点那条消息被撤销的部分）都不可见。
                if (id != revertMessage)
                {
                    continue;
                }

                reverted = false;
                if (revertPart is null)
                {
                    continue;
                }

                partLimit = revertPart;
            }

            using var message = JsonDocument.Parse(data);
            var role = StringField(message.RootElement, "role");
            var parts = VisibleTextParts(connection, id, partLimit);
            if (role == "user" && parts.Count > 0)
            {
                break;
            }

            if (role == "assistant" && !IsTrue(message.RootElement, "summary"))
            {
                texts.InsertRange(0, parts);
            }
        }

        return texts;
    }

    /// <summary>会话的撤销点：<c>revert</c> 列是 <c>{"messageID": …, "partID": …}</c>，没撤销时为空。</summary>
    private static (string? Message, string? Part) Revert(SqliteConnection connection, string sessionId)
    {
        if (Scalar(connection, "SELECT revert FROM session WHERE id = $id", sessionId) is not string json || json.Length == 0)
        {
            return (null, null);
        }

        try
        {
            using var revert = JsonDocument.Parse(json);
            return (StringField(revert.RootElement, "messageID"), StringField(revert.RootElement, "partID"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>消息里可见的文字块（不是 synthetic / ignored）；<paramref name="beforePart"/> 不为空时只取它之前的块。</summary>
    private static List<string> VisibleTextParts(SqliteConnection connection, string messageId, string? beforePart)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, data FROM part WHERE message_id = $id ORDER BY id";
        command.Parameters.AddWithValue("$id", messageId);
        var texts = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (beforePart is not null && string.CompareOrdinal(reader.GetString(0), beforePart) >= 0)
            {
                break;
            }

            using var part = JsonDocument.Parse(reader.GetString(1));
            var root = part.RootElement;
            if (StringField(root, "type") == "text" && !IsTrue(root, "synthetic") && !IsTrue(root, "ignored")
                && StringField(root, "text") is { } text && !string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text.Trim());
            }
        }

        return texts;
    }

    private static object? Scalar(SqliteConnection connection, string sql, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static string? StringField(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsTrue(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
