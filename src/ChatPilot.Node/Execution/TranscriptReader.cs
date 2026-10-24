using System.Text;
using System.Text.Json;

namespace ChatPilot.Node.Execution;

/// <summary>
/// 从各类 agent 的会话记录里取"最后一次用户输入之后，agent 说的所有文字"。
/// 只读文件末尾 4 MiB；格式不认识或没有内容时返回 null，由调用方退回到读终端。
/// </summary>
/// <remarks>会话记录格式随各 agent 的版本变化，所有字段都按"可能缺失、类型可能不对"处理。</remarks>
public static class TranscriptReader
{
    /// <summary>只读会话记录最后这么多字节：会话记录可能很大，最后一轮的回复在末尾。</summary>
    private const int TailBytes = 4 * 1024 * 1024;

    /// <summary>这种 agent 有没有能读的 JSONL 会话记录格式（opencode / kilo 是 SQLite，见 <see cref="OpenCodeStore"/>）。</summary>
    /// <param name="kind">herdr 上报的 agent 类型。</param>
    /// <returns>认识这种格式为 true；类型未知（null）为 false。</returns>
    public static bool Supports(string? kind) =>
        kind is "claude" or "codex" or "pi" or "kimi" or "qwen" or "droid" or "cursor" or "copilot" or "grok";

    /// <summary>按 agent 类型选择格式，取最后一条回复；超长时保留结尾部分。</summary>
    /// <param name="kind">agent 类型，决定按哪种格式读。</param>
    /// <param name="path">会话记录文件。</param>
    /// <param name="maxChars">回复最长多少字，超长时保留结尾部分，开头加 <c>…</c>。</param>
    /// <returns>最后一轮的回复，各段之间空一行；读不了文件、格式不认识或没有内容时为 null。</returns>
    public static string? LastReply(string? kind, string path, int maxChars)
    {
        if (ReadTailLines(path) is not { } lines)
        {
            return null;
        }

        var texts = kind switch
        {
            "claude" => FromClaude(lines),
            "codex" => FromCodex(lines),
            "pi" => FromPi(lines),
            "kimi" => FromKimi(lines),
            "qwen" => FromQwen(lines),
            "droid" => FromDroid(lines),
            "cursor" => FromCursor(lines),
            "copilot" => FromCopilot(lines),
            "grok" => FromGrok(lines),
            _ => [],
        };
        return texts.Count == 0 ? null : TakeTail(string.Join("\n\n", texts).Trim(), maxChars);
    }

    /// <summary>
    /// Claude Code（<c>~/.claude/projects/*/&lt;id&gt;.jsonl</c>）：<c>type</c> 为 user / assistant，正文在 <c>message.content</c>。
    /// 子 agent（isSidechain）、系统注入（isMeta）和压缩上下文时写入的摘要（isCompactSummary）不算；
    /// 只含 tool_result 的 user 记录不是真人输入。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复文字块；没有时为空列表。</returns>
    internal static List<string> FromClaude(IReadOnlyList<string> lines)
    {
        var texts = new List<string>();
        foreach (var root in ParseBackwards(lines))
        {
            if (IsTrue(root, "isSidechain") || IsTrue(root, "isMeta") || IsTrue(root, "isCompactSummary")
                || Object(root, "message") is not { } message)
            {
                continue;
            }

            var type = StringField(root, "type");
            if (type == "user" && IsClaudeHumanPrompt(message))
            {
                break;
            }

            if (type == "assistant")
            {
                AddText(texts, ContentText(message, "content", "text"));
            }
        }

        texts.Reverse();
        return texts;
    }

    /// <summary>
    /// Codex（<c>~/.codex/sessions/YYYY/MM/DD/rollout-*-&lt;id&gt;.jsonl</c>）：每行 <c>{type, payload}</c>。
    /// 回复是 <c>response_item</c> 里 role 为 assistant 的 message（<c>output_text</c>）；
    /// 一轮从 <c>event_msg</c> 的 task_started / user_message 开始。早期版本没有 payload 外壳，字段直接在根上。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复文字块；没有时为空列表。</returns>
    internal static List<string> FromCodex(IReadOnlyList<string> lines)
    {
        var texts = new List<string>();
        foreach (var root in ParseBackwards(lines))
        {
            var item = Object(root, "payload") ?? root;
            var itemType = StringField(item, "type");
            if (StringField(root, "type") == "event_msg" && itemType is "task_started" or "user_message")
            {
                break;
            }

            if (itemType != "message")
            {
                continue;
            }

            var role = StringField(item, "role");
            if (role == "user" && IsCodexHumanPrompt(item))
            {
                break;
            }

            if (role == "assistant")
            {
                AddText(texts, ContentText(item, "content", "output_text"));
            }
        }

        texts.Reverse();
        return texts;
    }

    /// <summary>
    /// pi（herdr 直接给出 <c>~/.pi/agent/sessions/…/*.jsonl</c> 路径）：会话是一棵树，每条记录有 id 和 parentId。
    /// 从最后一条记录沿 parentId 往回走，避免把用户分叉出去的其他分支算进来；正文在 <c>message.content</c> 的 text 块里。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复文字块；没有时为空列表。</returns>
    internal static List<string> FromPi(IReadOnlyList<string> lines)
    {
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in lines)
        {
            if (TryParse(line) is { } root && StringField(root, "id") is { } id)
            {
                byId[id] = root;
                current = id;
            }
        }

        var texts = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(current) && byId.TryGetValue(current, out var entry))
        {
            if (StringField(entry, "type") == "message" && Object(entry, "message") is { } message)
            {
                var role = StringField(message, "role");
                if (role == "user")
                {
                    break;
                }

                if (role == "assistant")
                {
                    AddText(texts, ContentText(message, "content", "text"));
                }
            }

            current = StringField(entry, "parentId");
        }

        texts.Reverse();
        return texts;
    }

    /// <summary>
    /// Kimi Code（<c>~/.kimi-code/sessions/*/session_&lt;id&gt;/agents/main/wire.jsonl</c>）：事件流。
    /// 回复是 <c>agent.message.appended</c> 里 role 为 assistant 的消息；用户输入是 <c>turn.prompt</c>，
    /// 或 meta.source 为 input 的 user 消息（提醒、通知等注入的 user 消息不算）。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复文字块；没有时为空列表。</returns>
    internal static List<string> FromKimi(IReadOnlyList<string> lines)
    {
        var texts = new List<string>();
        foreach (var root in ParseBackwards(lines))
        {
            var type = StringField(root, "type");
            if (type == "turn.prompt" && StringField(Object(root, "origin"), "kind") is null or "user")
            {
                break;
            }

            if (type != "agent.message.appended" || Object(root, "message") is not { } wrapper || Object(wrapper, "message") is not { } message)
            {
                continue;
            }

            var role = StringField(message, "role");
            if (role == "user" && StringField(Object(wrapper, "meta"), "source") == "input")
            {
                break;
            }

            if (role == "assistant")
            {
                AddText(texts, ContentText(message, "content", "text"));
            }
        }

        texts.Reverse();
        return texts;
    }

    /// <summary>
    /// Qwen Code（<c>~/.qwen/projects/&lt;目录&gt;/chats/&lt;id&gt;.jsonl</c>）：每行有 uuid / parentUuid，会话是一棵树（撤回后会分叉）。
    /// 同一个 uuid 可能分成几行写，正文要拼起来。从最后一条（不算会话快照之类的 system 记录）沿 parentUuid 往回走；
    /// 真人输入是没有 subtype（或是干活途中补充的 mid_turn_user_message）、来源为 real_user 的 user 记录；
    /// 回复是 assistant 记录里不是思考过程（thought）的文字块。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复文字块（同一条记录的分片已拼好）；没有时为空列表。</returns>
    internal static List<string> FromQwen(IReadOnlyList<string> lines)
    {
        var records = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        string? leaf = null;
        foreach (var line in lines)
        {
            if (TryParse(line) is not { } root || StringField(root, "uuid") is not { } uuid)
            {
                continue;
            }

            if (!records.TryGetValue(uuid, out var fragments))
            {
                records[uuid] = fragments = [];
            }

            fragments.Add(root);
            if (!IsQwenSnapshot(root))
            {
                leaf = uuid;
            }
        }

        var texts = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = leaf;
        while (current is not null && visited.Add(current) && records.TryGetValue(current, out var fragments))
        {
            var first = fragments[0];
            var type = StringField(first, "type");
            if (type == "user" && StringField(first, "subtype") is null or "mid_turn_user_message"
                && StringField(first, "provenance") is null or "real_user")
            {
                break;
            }

            if (type == "assistant")
            {
                var parts = fragments
                    .SelectMany(f => QwenTextParts(Object(f, "message")))
                    .Where(t => t != "(empty content)");
                AddText(texts, string.Join("\n\n", parts).Trim());
            }

            current = StringField(first, "parentUuid");
        }

        texts.Reverse();
        return texts;
    }

    /// <summary>会话快照之类的 system 记录不参与选最后一条（Qwen 自己选叶子时也跳过它们）。</summary>
    /// <param name="root">一行记录。</param>
    /// <returns>是这类 system 记录为 true。</returns>
    private static bool IsQwenSnapshot(JsonElement root) =>
        StringField(root, "type") == "system"
        && StringField(root, "subtype") is { } subtype
        && (subtype is "session_artifact_event" or "session_artifact_snapshot" or "session_sources_snapshot"
            || subtype.StartsWith("managed_session_", StringComparison.Ordinal));

    /// <summary>Gemini 风格的 <c>message.parts</c>：取 text，跳过 thought 为 true 的思考过程。</summary>
    /// <param name="message">记录里的 <c>message</c> 对象；为 null 时没有内容。</param>
    /// <returns>去掉首尾空白的非空文字块。</returns>
    private static IEnumerable<string> QwenTextParts(JsonElement? message)
    {
        if (message is not { } m || !m.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && !IsTrue(part, "thought")
                && StringField(part, "text") is { } text && !string.IsNullOrWhiteSpace(text))
            {
                yield return text.Trim();
            }
        }
    }

    /// <summary>
    /// Droid（<c>~/.factory/sessions/&lt;目录&gt;/&lt;id&gt;.jsonl</c>）：<c>{type: "message", message: {role, content}}</c>，content 是 Anthropic 风格的块。
    /// 真人输入是 role 为 user、没有 hookEventName、对双方都可见、不含 tool_result，
    /// 并且有一段文字不只是 <c>&lt;system-reminder&gt;</c> / <c>&lt;system-notification&gt;</c> 的记录（和 Droid 自己的判断一致）。
    /// 只给模型看的（visibility 为 llm_only）回复不算。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复文字块；没有时为空列表。</returns>
    internal static List<string> FromDroid(IReadOnlyList<string> lines)
    {
        var texts = new List<string>();
        foreach (var root in ParseBackwards(lines))
        {
            if (StringField(root, "type") != "message" || Object(root, "message") is not { } message)
            {
                continue;
            }

            var role = StringField(message, "role");
            var visibility = StringField(message, "visibility");
            if (role == "user" && visibility is not ("llm_only" or "user_only") && StringField(message, "hookEventName") is null
                && !HasPart(message, "tool_result")
                && ContentParts(message, "content", "text").Any(t => !IsInjectedBlock(t)))
            {
                break;
            }

            if (role == "assistant" && visibility != "llm_only")
            {
                AddText(texts, ContentText(message, "content", "text"));
            }
        }

        texts.Reverse();
        return texts;
    }

    /// <summary>
    /// Cursor Agent（<c>~/.cursor/projects/&lt;目录&gt;/agent-transcripts/&lt;id&gt;/&lt;id&gt;.jsonl</c>）：每行 <c>{role, message: {content}}</c>。
    /// Cursor 写这份记录时已经去掉了系统消息和工具结果，user 行就是真人输入；没有 role 的行（例如 turn_ended）跳过。
    /// 注意 Cursor 会把思考过程接在回复正文后面写进同一个文字块，无法拆开。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复文字块；没有时为空列表。</returns>
    internal static List<string> FromCursor(IReadOnlyList<string> lines)
    {
        var texts = new List<string>();
        foreach (var root in ParseBackwards(lines))
        {
            var role = StringField(root, "role");
            if (role == "user")
            {
                break;
            }

            if (role == "assistant" && Object(root, "message") is { } message)
            {
                AddText(texts, ContentText(message, "content", "text"));
            }
        }

        texts.Reverse();
        return texts;
    }

    /// <summary>
    /// GitHub Copilot CLI（<c>~/.copilot/session-state/&lt;id&gt;/events.jsonl</c>）：事件流 <c>{type, data, agentId?}</c>。
    /// 真人输入是没有 agentId、data.source 为空、不是自动续跑（isAutopilotContinuation）的 user.message；
    /// 回复是主 agent（没有 agentId / parentToolCallId）的 assistant.message 的 content。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复文字块；没有时为空列表。</returns>
    internal static List<string> FromCopilot(IReadOnlyList<string> lines)
    {
        var texts = new List<string>();
        foreach (var root in ParseBackwards(lines))
        {
            var type = StringField(root, "type");
            if (StringField(root, "agentId") is not null || Object(root, "data") is not { } data)
            {
                continue;
            }

            if (type == "user.message" && string.IsNullOrEmpty(StringField(data, "source")) && !IsTrue(data, "isAutopilotContinuation"))
            {
                break;
            }

            if (type == "assistant.message" && StringField(data, "parentToolCallId") is null && StringField(data, "content") is { } content)
            {
                AddText(texts, content.Trim());
            }
        }

        texts.Reverse();
        return texts;
    }

    /// <summary>
    /// Grok Build（<c>~/.grok/sessions/&lt;目录&gt;/&lt;id&gt;/updates.jsonl</c>）：ACP 的 session/update 流，
    /// <c>params.update.sessionUpdate</c> 为 user_message_chunk / agent_message_chunk / tool_call 等。
    /// 连续的 agent_message_chunk 是同一条消息的分片，直接拼接；中间隔着工具调用的算不同消息。
    /// </summary>
    /// <param name="lines">会话记录末尾的各行。</param>
    /// <returns>按时间先后排列的回复消息（分片已拼好）；没有时为空列表。</returns>
    internal static List<string> FromGrok(IReadOnlyList<string> lines)
    {
        var texts = new List<string>();
        var current = new List<string>();
        foreach (var root in ParseBackwards(lines))
        {
            if (StringField(root, "method") != "session/update" || Object(Object(root, "params"), "update") is not { } update)
            {
                continue;
            }

            var kind = StringField(update, "sessionUpdate");
            if (kind == "user_message_chunk")
            {
                break;
            }

            if (kind == "agent_message_chunk")
            {
                current.Insert(0, StringField(Object(update, "content"), "text") ?? "");
                continue;
            }

            if (kind != "agent_thought_chunk")
            {
                AddText(texts, string.Concat(current).Trim());
                current.Clear();
            }
        }

        AddText(texts, string.Concat(current).Trim());
        texts.Reverse();
        return texts;
    }

    /// <summary>整段都是注入的提醒或通知（<c>&lt;system-reminder&gt;…</c>、<c>&lt;system-notification&gt;…</c>）。</summary>
    /// <param name="text">一个文字块。</param>
    /// <returns>整段被这两种标签包住为 true。</returns>
    private static bool IsInjectedBlock(string text)
    {
        var trimmed = text.Trim();
        return (trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal) && trimmed.EndsWith("</system-reminder>", StringComparison.Ordinal))
            || (trimmed.StartsWith("<system-notification>", StringComparison.Ordinal) && trimmed.EndsWith("</system-notification>", StringComparison.Ordinal));
    }

    /// <summary>content 数组里有没有指定类型的块。</summary>
    /// <param name="message">消息对象。</param>
    /// <param name="partType">块类型，例如 <c>tool_result</c>。</param>
    /// <returns>有这种块为 true；content 不是数组时为 false。</returns>
    private static bool HasPart(JsonElement message, string partType) =>
        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
        && content.EnumerateArray().Any(b => StringField(b, "type") == partType);

    /// <summary>读文件末尾（最多 4 MiB）并按行拆分；从中间开始读时丢掉第一行残缺的部分。读不了返回 null。</summary>
    /// <param name="path">会话记录文件。</param>
    /// <returns>非空的各行（agent 正在写也能读）；读不了时为 null。</returns>
    internal static List<string>? ReadTailLines(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
            if (start > 0)
            {
                var firstBreak = text.IndexOf('\n', StringComparison.Ordinal);
                text = firstBreak >= 0 ? text[(firstBreak + 1)..] : "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>从后往前逐行解析 JSON 对象，跳过坏行。</summary>
    /// <param name="lines">会话记录的各行。</param>
    /// <returns>从最新到最旧的记录。</returns>
    private static IEnumerable<JsonElement> ParseBackwards(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (TryParse(lines[i]) is { } root)
            {
                yield return root;
            }
        }
    }

    /// <summary>解析一行 JSON；不是对象或格式不对时返回 null。返回的是独立副本，不依赖 JsonDocument 的生命周期。</summary>
    /// <param name="line">一行会话记录。</param>
    /// <returns>解析出的对象；不是对象或格式不对时为 null。</returns>
    private static JsonElement? TryParse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Claude 的真人输入：content 是字符串，或者是含 text 块、不含 tool_result 的数组。</summary>
    /// <param name="message">user 记录的 <c>message</c> 对象。</param>
    /// <returns>是真人输入为 true；只是工具结果或没有 content 时为 false。</returns>
    private static bool IsClaudeHumanPrompt(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return false;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return true;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var hasText = false;
        foreach (var block in content.EnumerateArray())
        {
            var blockType = StringField(block, "type");
            if (blockType == "tool_result")
            {
                return false;
            }

            hasText |= blockType == "text";
        }

        return hasText;
    }

    /// <summary>
    /// Codex 的真人输入：role 为 user，且不是 Codex 自己注入的上下文
    /// （AGENTS.md 说明、<c>&lt;environment_context&gt;</c> 等以 <c>#</c> 或 <c>&lt;</c> 开头的块）。
    /// </summary>
    /// <param name="item">role 为 user 的 message。</param>
    /// <returns>至少有一个 <c>input_text</c> 块不是注入的上下文为 true。</returns>
    private static bool IsCodexHumanPrompt(JsonElement item) =>
        ContentParts(item, "content", "input_text").Any(t =>
            !t.StartsWith("# AGENTS.md", StringComparison.Ordinal) && !t.TrimStart().StartsWith('<'));

    /// <summary>content 里指定类型的文字块拼起来；content 是字符串时直接用。</summary>
    /// <param name="message">消息对象。</param>
    /// <param name="property">content 所在的属性名。</param>
    /// <param name="partType">要取的块类型，例如 <c>text</c>、<c>output_text</c>。</param>
    /// <returns>拼好的文字，块之间空一行；没有时为空字符串。</returns>
    private static string ContentText(JsonElement message, string property, string partType) =>
        string.Join("\n\n", ContentParts(message, property, partType)).Trim();

    /// <summary>content 里指定类型的各个文字块；content 是字符串时就是它本身。</summary>
    /// <param name="message">消息对象。</param>
    /// <param name="property">content 所在的属性名。</param>
    /// <param name="partType">要取的块类型；content 是字符串时不看。</param>
    /// <returns>去掉首尾空白的非空文字块；没有这个属性或类型不对时为空。</returns>
    private static IEnumerable<string> ContentParts(JsonElement message, string property, string partType)
    {
        if (!message.TryGetProperty(property, out var content))
        {
            yield break;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is { } single && !string.IsNullOrWhiteSpace(single))
            {
                yield return single.Trim();
            }

            yield break;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (StringField(block, "type") == partType && StringField(block, "text") is { } text && !string.IsNullOrWhiteSpace(text))
            {
                yield return text.Trim();
            }
        }
    }

    /// <summary>非空的文字才加进去，避免拼接时多出空段落。</summary>
    /// <param name="texts">收集到的文字块。</param>
    /// <param name="text">要加的文字。</param>
    private static void AddText(List<string> texts, string text)
    {
        if (text.Length > 0)
        {
            texts.Add(text);
        }
    }

    /// <summary>超长时保留结尾部分，开头加 <c>…</c>。</summary>
    /// <param name="text">完整的回复。</param>
    /// <param name="maxChars">最多多少字（含 <c>…</c>）。</param>
    /// <returns>不超过 <paramref name="maxChars"/> 字的文字。</returns>
    private static string TakeTail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "…" + text[^(maxChars - 1)..];

    /// <summary>安全地取对象字段：不是对象、没有字段或字段不是对象时返回 null。</summary>
    /// <param name="element">JSON 元素；为 null 时直接返回 null，方便连着取多层。</param>
    /// <param name="property">属性名。</param>
    /// <returns>字段值；取不到时为 null。</returns>
    private static JsonElement? Object(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>安全地取字符串字段：不是对象、没有字段或类型不对时返回 null。</summary>
    /// <param name="element">JSON 元素；可以为 null。</param>
    /// <param name="property">属性名。</param>
    /// <returns>字段值；取不到时为 null。</returns>
    private static string? StringField(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>对象的这个字段是不是 <c>true</c>。</summary>
    /// <param name="element">JSON 对象（调用方保证是对象）。</param>
    /// <param name="property">属性名。</param>
    /// <returns>字段值为 <c>true</c> 时为 true；没有字段或是其他值时为 false。</returns>
    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
