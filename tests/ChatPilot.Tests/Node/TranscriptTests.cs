using System.Text.Json;
using ChatPilot.Node.Config;
using ChatPilot.Node.Execution;
using ChatPilot.Node.Herdr;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatPilot.Tests.Node;

/// <summary>从各类 agent 的会话记录里取最后一条回复（F-17）。记录结构取自本机真实的 Claude Code / Codex / pi / Kimi Code 会话。</summary>
public sealed class TranscriptReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Claude_collects_assistant_text_after_last_human_prompt()
    {
        var path = Write(
            new { type = "user", message = new { role = "user", content = "旧问题" } },
            new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "旧回答" } } } },
            new { type = "user", message = new { role = "user", content = new object[] { new { type = "text", text = "新问题" } } } },
            new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "我先看看" }, new { type = "tool_use", name = "Read" } } } },
            new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", content = "file" } } } },
            new { type = "assistant", isSidechain = true, message = new { role = "assistant", content = new object[] { new { type = "text", text = "子任务的话" } } } },
            new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "改好了" } } } });

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("claude", path, 1000));
        Assert.Null(TranscriptReader.LastReply("claude", _temp.Combine("missing.jsonl"), 1000));
    }

    [Fact]
    public void Claude_idle_compaction_summary_is_not_a_new_prompt()
    {
        // 空闲时压缩上下文会追加一条 isCompactSummary 的 user 记录，最后一条回复仍然是压缩之前的那条。
        var path = Write(
            new { type = "user", message = new { role = "user", content = "打包" } },
            new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "打包好了" } } } },
            new { type = "system", subtype = "compact_boundary" },
            new { type = "user", isCompactSummary = true, message = new { role = "user", content = "This session is being continued from a previous conversation…" } });

        Assert.Equal("打包好了", TranscriptReader.LastReply("claude", path, 1000));
    }

    [Fact]
    public void Codex_starts_the_turn_at_task_started_and_skips_injected_context()
    {
        var path = Write(
            new { type = "session_meta", payload = new { id = "s1" } },
            Event("task_started"),
            Message("user", "input_text", "旧问题"),
            Message("assistant", "output_text", "旧回答"),
            Event("task_complete"),
            Event("task_started"),
            Message("developer", "input_text", "<permissions instructions>…"),
            Message("user", "input_text", "<environment_context>\n  <cwd>/x</cwd>\n</environment_context>"),
            Message("user", "input_text", "新问题"),
            new { type = "response_item", payload = new { type = "reasoning", summary = Array.Empty<object>() } },
            Message("assistant", "output_text", "我先看看"),
            new { type = "response_item", payload = new { type = "custom_tool_call", name = "exec", input = "ls" } },
            new { type = "response_item", payload = new { type = "custom_tool_call_output", output = "a.cs" } },
            Message("assistant", "output_text", "改好了"),
            Event("task_complete"));

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("codex", path, 1000));
    }

    [Fact]
    public void Codex_user_message_mid_turn_is_a_boundary_and_legacy_lines_are_understood()
    {
        // 干活途中补充的话没有新的 task_started；早期版本的记录没有 payload 外壳。
        var path = Write(
            Event("task_started"),
            Message("user", "input_text", "先做 A"),
            Message("assistant", "output_text", "在做 A"),
            Message("user", "input_text", "顺便做 B"),
            new { type = "message", role = "assistant", content = new object[] { new { type = "output_text", text = "A 和 B 都做完了" } } });

        Assert.Equal("A 和 B 都做完了", TranscriptReader.LastReply("codex", path, 1000));
    }

    [Fact]
    public void Pi_follows_the_current_branch_of_the_session_tree()
    {
        // 用户回到 a1 另开了分支 m3；之后追加的 other 属于旧分支，不能混进这一轮的回复。
        var path = Write(
            new { type = "session", id = "s", cwd = "/x" },
            Pi("m1", null, "user", Text("问题一")),
            Pi("a1", "m1", "assistant", Text("回答一")),
            Pi("m2", "a1", "user", Text("旧分支的问题")),
            Pi("m3", "a1", "user", Text("新问题")),
            Pi("a2", "m3", "assistant", new { type = "thinking", thinking = "想一想" }, Text("我先看看"), new { type = "toolCall", name = "read" }),
            Pi("other", "m2", "assistant", Text("旧分支的回答")),
            Pi("t1", "a2", "toolResult", Text("文件内容")),
            new { type = "model_change", id = "c1", parentId = "t1", modelId = "glm" },
            Pi("a3", "c1", "assistant", Text("改好了")));

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("pi", path, 1000));
    }

    [Fact]
    public void Kimi_reads_appended_assistant_messages_since_the_last_user_prompt()
    {
        var path = Write(
            new { type = "metadata", protocol_version = "1.5" },
            new { type = "turn.prompt", origin = new { kind = "user" } },
            Kimi("user", "input", Text("旧问题")),
            Kimi("assistant", "llm", Text("旧回答")),
            new { type = "turn.prompt", origin = new { kind = "user" } },
            Kimi("user", "input", Text("新问题")),
            Kimi("user", "notify", Text("<notification>后台任务结束</notification>")),
            Kimi("assistant", "llm", new { type = "think", think = "先看文件" }, Text("我先看看")),
            Kimi("tool", "tool", Text("a.cs")),
            Kimi("assistant", "llm", Text("改好了")));

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("kimi", path, 1000));
    }

    [Fact]
    public void Qwen_follows_the_uuid_chain_joins_fragments_and_skips_thoughts()
    {
        // 结构取自 Qwen Code 0.26 的 ChatRecordingService：u2 之后撤回（rewind）到 a1 重新提问，旧分支留在文件里。
        var path = Write(
            Qwen("u1", null, "user", "real_user", null, new { text = "旧问题" }),
            Qwen("a1", "u1", "assistant", "assistant_output", null, new { text = "旧回答" }),
            Qwen("u2", "a1", "user", "real_user", null, new { text = "被撤回的问题" }),
            Qwen("a2", "u2", "assistant", "assistant_output", null, new { text = "被撤回的回答" }),
            Qwen("r1", "a2", "system", "system", "rewind"),
            Qwen("u3", "a1", "user", "real_user", null, new { text = "新问题" }),
            Qwen("n1", "u3", "user", "system", "notification", new { text = "<notification>后台任务结束</notification>" }),
            Qwen("a3", "n1", "assistant", "assistant_output", null, new { text = "先想想", thought = true }, new { text = "我先看看" }),
            Qwen("a3", "n1", "assistant", "assistant_output", null, new { functionCall = new { name = "read_file" } }),
            Qwen("t1", "a3", "tool_result", "tool_result", null, new { functionResponse = new { name = "read_file" } }),
            Qwen("a4", "t1", "assistant", "assistant_output", null, new { text = "改好了" }),
            Qwen("s1", "a4", "system", "system", "session_artifact_snapshot"));

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("qwen", path, 1000));
    }

    [Fact]
    public void Droid_skips_hook_reminder_and_tool_result_user_records()
    {
        // 结构取自 Factory Droid 0.248 的会话文件。
        var path = Write(
            new { type = "session_start", id = "s", version = 2 },
            Droid("user", null, null, Text("旧问题")),
            Droid("assistant", null, null, Text("旧回答")),
            Droid("user", null, null, Text("新问题"), Text("<system-reminder>图片路径</system-reminder>")),
            Droid("assistant", null, null, new { type = "thinking", thinking = "想一想" }, Text("我先看看"), new { type = "tool_use", name = "Execute" }),
            Droid("user", null, null, new { type = "tool_result", tool_use_id = "t1", content = "ok" }),
            Droid("user", null, "PostToolUse", Text("hook 的输出")),
            Droid("user", null, null, Text("<system-reminder>只是提醒</system-reminder>")),
            Droid("assistant", "llm_only", null, Text("只给模型看的话")),
            new { type = "agent_turn_outcome" },
            Droid("assistant", null, null, Text("改好了")));

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("droid", path, 1000));
    }

    [Fact]
    public void Cursor_reads_role_lines_and_skips_turn_markers()
    {
        // 结构取自 Cursor Agent CLI 的 agent-transcripts 写入代码。
        var path = Write(
            CursorLine("user", Text("<user_query>\n旧问题\n</user_query>")),
            CursorLine("assistant", Text("旧回答")),
            new { type = "turn_ended", status = "success" },
            CursorLine("user", Text("<timestamp>Thursday</timestamp>\n<user_query>\n新问题\n</user_query>")),
            CursorLine("assistant", Text("我先看看"), new { type = "tool_use", name = "Shell" }),
            CursorLine("assistant", Text("改好了")),
            new { type = "turn_ended", status = "success" });

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("cursor", path, 1000));
    }

    [Fact]
    public void Copilot_ignores_subagents_tool_children_and_autopilot_continuations()
    {
        // 结构取自 @github/copilot 的 session-events 模式。
        var path = Write(
            Copilot("user.message", null, new { content = "旧问题" }),
            Copilot("assistant.message", null, new { content = "旧回答" }),
            Copilot("user.message", null, new { content = "新问题" }),
            Copilot("assistant.turn_start", null, new { turnId = "t" }),
            Copilot("assistant.message", null, new { content = "我先看看", toolRequests = new[] { new { name = "bash" } } }),
            Copilot("assistant.message", "sub-1", new { content = "子 agent 的话" }),
            Copilot("assistant.message", null, new { content = "工具里的话", parentToolCallId = "c1" }),
            Copilot("user.message", null, new { content = "继续", isAutopilotContinuation = true }),
            Copilot("user.message", null, new { content = "/review", source = "command-review" }),
            Copilot("assistant.message", null, new { content = "" }),
            Copilot("assistant.message", null, new { content = "改好了" }));

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("copilot", path, 1000));
    }

    [Fact]
    public void Grok_joins_message_chunks_and_splits_on_tool_calls()
    {
        // 结构取自 Grok Build 文档里的 ACP session/update 流。
        var path = Write(
            GrokUpdate("user_message_chunk", "旧问题"),
            GrokUpdate("agent_message_chunk", "旧回答"),
            GrokUpdate("user_message_chunk", "新问题"),
            GrokUpdate("agent_thought_chunk", "想一想"),
            GrokUpdate("agent_message_chunk", "我先"),
            GrokUpdate("agent_message_chunk", "看看"),
            GrokUpdate("tool_call", null),
            new { method = "_x.ai/session/update", @params = new { update = new { sessionUpdate = "retry_state" } } },
            GrokUpdate("agent_message_chunk", "改好了"),
            new { method = "_x.ai/session/update", @params = new { update = new { sessionUpdate = "turn_completed" } } });

        Assert.Equal("我先看看\n\n改好了", TranscriptReader.LastReply("grok", path, 1000));
    }

    [Fact]
    public void Unsupported_kinds_and_empty_turns_return_null()
    {
        var path = Write(Event("task_started"), Message("user", "input_text", "问题"));
        Assert.Null(TranscriptReader.LastReply("codex", path, 1000));
        Assert.Null(TranscriptReader.LastReply("opencode", path, 1000));
    }

    [Fact]
    public void Long_replies_keep_the_tail()
    {
        var path = Write(Event("task_started"), Message("user", "input_text", "问题"), Message("assistant", "output_text", new string('a', 500) + "END"));
        var reply = TranscriptReader.LastReply("codex", path, 100);
        Assert.Equal(100, reply?.Length);
        Assert.EndsWith("END", reply, StringComparison.Ordinal);
    }

    private string Write(params object[] entries)
    {
        var path = _temp.Combine(Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, entries.Select(e => JsonSerializer.Serialize(e)).Append("{broken json"));
        return path;
    }

    private static object Text(string text) => new { type = "text", text };

    private static object Event(string type) => new { type = "event_msg", payload = new { type } };

    private static object Message(string role, string partType, string text) =>
        new { type = "response_item", payload = new { type = "message", role, content = new object[] { new { type = partType, text } } } };

    private static object Pi(string id, string? parentId, string role, params object[] content) =>
        new { type = "message", id, parentId, message = new { role, content } };

    private static object Kimi(string role, string source, params object[] content) =>
        new { type = "agent.message.appended", message = new { message = new { role, content }, meta = new { source } } };

    private static object Qwen(string uuid, string? parentUuid, string type, string provenance, string? subtype, params object[] parts) =>
        new { uuid, parentUuid, type, provenance, subtype, message = new { role = type == "assistant" ? "model" : "user", parts } };

    private static object Droid(string role, string? visibility, string? hookEventName, params object[] content) =>
        new { type = "message", message = new { role, content, visibility, hookEventName } };

    private static object CursorLine(string role, params object[] content) => new { role, message = new { content } };

    private static object Copilot(string type, string? agentId, object data) => new { type, agentId, data };

    private static object GrokUpdate(string kind, string? text) =>
        new
        {
            method = "session/update",
            @params = new { update = new { sessionUpdate = kind, content = text is null ? null : new { type = "text", text } } },
        };
}

/// <summary>opencode / kilo 的 SQLite 会话库（表结构取自 opencode 1.19 的 session / message / part 表）。</summary>
public sealed class OpenCodeStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _database;
    private long _clock = 1_792_745_000_000;

    public OpenCodeStoreTests()
    {
        _database = _temp.Combine("opencode.db");
        Execute("""
            CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, revert TEXT);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL, time_created INTEGER NOT NULL, data TEXT NOT NULL);
            CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL, data TEXT NOT NULL);
            """);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void Collects_assistant_text_after_the_last_human_prompt()
    {
        Session("ses_a");
        Message("ses_a", "msg_1", "user", Text("旧问题"));
        Message("ses_a", "msg_2", "assistant", Text("旧回答"));
        Message("ses_a", "msg_3", "user", Text("新问题"));
        Message("ses_a", "msg_4", "assistant", new { type = "reasoning", text = "想一想" }, Text("我先看看"), new { type = "tool", tool = "read" });
        Message("ses_a", "msg_5", "user", new { type = "text", text = "<shell 输出>", synthetic = true });
        Message("ses_a", "msg_6", "assistant", Text("改好了"));
        SummaryMessage("ses_a", "msg_7", Text("压缩摘要"));

        Assert.True(OpenCodeStore.HasSession(_database, "ses_a"));
        Assert.False(OpenCodeStore.HasSession(_database, "ses_missing"));
        Assert.Equal("我先看看\n\n改好了", OpenCodeStore.LastReply(_database, "ses_a", 1000));
    }

    [Fact]
    public void Child_sessions_resolve_to_the_root_and_reverted_messages_are_hidden()
    {
        Session("ses_root", revert: """{"messageID":"msg_4"}""");
        Session("ses_child", parent: "ses_root");
        Message("ses_root", "msg_1", "user", Text("问题"));
        Message("ses_root", "msg_2", "assistant", Text("回答"));
        Message("ses_root", "msg_3", "user", Text("被撤销的问题"));
        Message("ses_root", "msg_4", "assistant", Text("被撤销的回答"));
        Message("ses_child", "msg_9", "assistant", Text("子 agent 的话"));

        // 撤销点 msg_4 之后都不可见，于是最后一次真人输入是 msg_3，它之后没有可见的回复。
        Assert.Null(OpenCodeStore.LastReply(_database, "ses_child", 1000));
        Execute("UPDATE session SET revert = '{\"messageID\":\"msg_3\"}' WHERE id = 'ses_root'");
        Assert.Equal("回答", OpenCodeStore.LastReply(_database, "ses_child", 1000));
    }

    [Fact]
    public void Unreadable_databases_return_null()
    {
        var notDb = _temp.Combine("broken.db");
        File.WriteAllText(notDb, "not a database");
        Assert.False(OpenCodeStore.HasSession(notDb, "ses_a"));
        Assert.Null(OpenCodeStore.LastReply(notDb, "ses_a", 1000));
    }

    private static object Text(string text) => new { type = "text", text };

    private void Session(string id, string? parent = null, string? revert = null) =>
        Execute("INSERT INTO session (id, parent_id, revert) VALUES ($a, $b, $c)", id, parent, revert);

    private void Message(string session, string id, string role, params object[] parts) => AddMessage(session, id, new { role }, parts);

    /// <summary>压缩上下文时写入的摘要消息（summary 为 true）。</summary>
    private void SummaryMessage(string session, string id, params object[] parts) =>
        AddMessage(session, id, new { role = "assistant", summary = true }, parts);

    private void AddMessage(string session, string id, object data, object[] parts)
    {
        Execute("INSERT INTO message (id, session_id, time_created, data) VALUES ($a, $b, $c, $d)", id, session, _clock++, JsonSerializer.Serialize(data));
        var index = 0;
        foreach (var part in parts)
        {
            Execute("INSERT INTO part (id, message_id, session_id, data) VALUES ($a, $b, $c, $d)", $"prt_{id}_{index++}", id, session, JsonSerializer.Serialize(part));
        }
    }

    private void Execute(string sql, params object?[] values)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var names = new[] { "$a", "$b", "$c", "$d" };
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue(names[i], values[i] ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}

/// <summary>根据 herdr 给的会话引用找到会话记录文件。</summary>
public sealed class TranscriptLocatorTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Codex_session_id_is_found_under_dated_folders_and_codex_home_is_honoured()
    {
        const string id = "01a143ac-9ab1-78d2-8992-ac635ffce2aa";
        var expected = Touch(_home.Combine(".codex", "sessions", "2026", "10", "16", $"rollout-2026-10-16T16-45-47-{id}.jsonl"));
        Assert.Equal(expected, Locator().Find(Agent("codex", "id", id))?.Path);

        var custom = _home.CreateDirectory("custom-codex");
        _environment["CODEX_HOME"] = custom;
        Assert.Null(Locator().Find(Agent("codex", "id", id))?.Path);
        var archived = Touch(Path.Combine(custom, "archived_sessions", $"rollout-2026-10-01T00-00-00-{id}.jsonl"));
        Assert.Equal(archived, Locator().Find(Agent("codex", "id", id))?.Path);
    }

    [Fact]
    public void Kimi_session_id_with_or_without_prefix_is_found()
    {
        const string uuid = "2dc68d1a-b1c0-46ab-a3b0-b5aa553bc2eb";
        var expected = Touch(_home.Combine(".kimi-code", "sessions", "wd_demo_7660", "session_" + uuid, "agents", "main", "wire.jsonl"));
        Assert.Equal(expected, Locator().Find(Agent("kimi", "id", "session_" + uuid))?.Path);
        Assert.Equal(expected, Locator().Find(Agent("kimi", "id", uuid))?.Path);
    }

    [Fact]
    public void Claude_session_id_is_found_in_projects()
    {
        const string id = "bc2bcc69-eaa1-489f-adb5-33350c9d1e2e";
        var expected = Touch(_home.Combine(".claude", "projects", "-Users-me-demo", id + ".jsonl"));
        Assert.Equal(expected, Locator().Find(Agent("claude", "id", id))?.Path);
    }

    [Fact]
    public void Qwen_session_is_found_in_any_project_folder_or_archive()
    {
        const string id = "0f6d2c9e-7b1a-4c55-9d1e-2a3b4c5d6e7f";
        var archived = Touch(_home.Combine(".qwen", "projects", "d-----chatpilot", "chats", "archive", id + ".jsonl"));
        Assert.Equal(archived, Locator().Find(Agent("qwen", "id", id))?.Path);

        _environment["QWEN_RUNTIME_DIR"] = _home.CreateDirectory("qwen-runtime");
        var live = Touch(_home.Combine("qwen-runtime", "projects", "d-----other", "chats", id + ".jsonl"));
        Assert.Equal(live, Locator().Find(Agent("qwen", "id", id))?.Path);
    }

    [Fact]
    public void OpenCode_database_is_picked_by_session_and_xdg_data_home_is_honoured()
    {
        _environment["XDG_DATA_HOME"] = _home.CreateDirectory("data");
        var empty = CreateOpenCodeDatabase(_home.Combine("data", "opencode", "opencode.db"), null);
        var beta = CreateOpenCodeDatabase(_home.Combine("data", "opencode", "opencode-beta.db"), "ses_beta");
        var source = Locator().Find(Agent("opencode", "id", "ses_beta"));
        Assert.Equal(beta, source?.Path);
        Assert.Equal("ses_beta", source?.SessionId);
        Assert.Null(Locator().Find(Agent("opencode", "id", "ses_missing")));
        Assert.True(File.Exists(empty));

        var kilo = CreateOpenCodeDatabase(_home.Combine("data", "kilo", "kilo.db"), "ses_kilo");
        Assert.Equal(kilo, Locator().Find(Agent("kilo", "id", "ses_kilo"))?.Path);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Droid_cursor_copilot_and_grok_sessions_are_found_by_id()
    {
        const string id = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";
        var droid = Touch(_home.Combine(".factory", "sessions", "-D-code-demo", id + ".jsonl"));
        var cursor = Touch(_home.Combine(".cursor", "projects", "D-code-demo", "agent-transcripts", id, id + ".jsonl"));
        var copilot = Touch(_home.Combine(".copilot", "session-state", id, "events.jsonl"));
        var grok = Touch(_home.Combine(".grok", "sessions", "D%3A%5Ccode%5Cdemo", id, "updates.jsonl"));

        Assert.Equal(droid, Locator().Find(Agent("droid", "id", id))?.Path);
        Assert.Equal(cursor, Locator().Find(Agent("cursor", "id", id))?.Path);
        Assert.Equal(copilot, Locator().Find(Agent("copilot", "id", id))?.Path);
        Assert.Equal(grok, Locator().Find(Agent("grok", "id", id))?.Path);

        _environment["COPILOT_HOME"] = _home.CreateDirectory("custom-copilot");
        Assert.Null(Locator().Find(Agent("copilot", "id", id)));
    }

    [Fact]
    public void Paths_are_used_directly_but_ids_cannot_escape_the_folders()
    {
        var piSession = Touch(_home.Combine("pi", "2026-10-14T11-03-03-239Z_01a13a14.jsonl"));
        Assert.Equal(piSession, Locator().Find(Agent("pi", "path", piSession))?.Path);
        Assert.Null(Locator().Find(Agent("pi", "path", _home.Combine("pi", "missing.jsonl")))?.Path);

        Touch(_home.Combine(".kimi-code", "sessions", "x", "..", "agents", "main", "wire.jsonl"));
        Assert.Null(Locator().Find(Agent("kimi", "id", ".."))?.Path);
        Assert.Null(Locator().Find(Agent("codex", "id", "../../etc"))?.Path);
        Assert.Null(Locator().Find(Agent("opencode", "id", "ses_1"))?.Path);
        Assert.Null(Locator().Find(Agent("codex", null, null))?.Path);
    }

    private TranscriptLocator Locator() =>
        new(new NodeConfig(), NullLogger.Instance, _home.Path, name => _environment.GetValueOrDefault(name));

    private static HerdrAgent Agent(string kind, string? sessionKind, string? value) =>
        new("w1:p1", null, kind, AgentStatus.Idle, "w1", null, null,
            sessionKind is null || value is null ? null : new HerdrAgentSession(sessionKind, value));

    private static string CreateOpenCodeDatabase(string path, string? sessionId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, revert TEXT);"
            + (sessionId is null ? "" : $"INSERT INTO session (id) VALUES ('{sessionId}');");
        command.ExecuteNonQuery();
        return path;
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}\n");
        return path;
    }
}
