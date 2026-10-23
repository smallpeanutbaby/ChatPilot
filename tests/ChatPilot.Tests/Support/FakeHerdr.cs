using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ChatPilot.Node.Herdr;

namespace ChatPilot.Tests.Support;

/// <summary>
/// 模拟的 herdr 命令行：在内存里维护 agent 和工作区，按真实 herdr 的 JSON 格式返回，并记录每次调用的参数。
/// </summary>
internal sealed class FakeHerdr : IProcessRunner
{
    private readonly Lock _lock = new();
    private readonly List<Agent> _agents = [];
    private readonly Dictionary<string, string> _workspaces = new(StringComparer.Ordinal);
    private int _nextWorkspace = 1;

    public sealed class Agent
    {
        public required string PaneId { get; init; }

        public string? Name { get; set; }

        public string Kind { get; set; } = "claude";

        public string Status { get; set; } = "idle";

        public required string WorkspaceId { get; init; }

        public string Cwd { get; set; } = "/tmp";

        public string? Title { get; set; }
    }

    public ConcurrentQueue<string[]> Calls { get; } = new();

    /// <summary>agent read 返回的终端内容。</summary>
    public string Screen { get; set; } = SampleScreens.ClaudeReply;

    /// <summary>agent prompt --wait 等待的时间。</summary>
    public TimeSpan PromptDelay { get; set; } = TimeSpan.Zero;

    /// <summary>agent prompt 完成后 agent 的状态。</summary>
    public string StatusAfterPrompt { get; set; } = "idle";

    /// <summary>让 agent prompt 返回这个错误码。</summary>
    public string? PromptError { get; set; }

    /// <summary>agent start 时卡在确认框（返回 agent_not_ready）。</summary>
    public bool StartBlocked { get; set; }

    /// <summary>所有命令额外等待的时间（模拟节点很慢）。</summary>
    public TimeSpan CommandDelay { get; set; } = TimeSpan.Zero;

    public Agent AddAgent(string? name, string label, string status = "idle", string kind = "claude")
    {
        lock (_lock)
        {
            var workspace = "w" + _nextWorkspace++.ToString(CultureInfo.InvariantCulture);
            _workspaces[workspace] = label;
            var agent = new Agent { PaneId = workspace + ":p1", Name = name, Status = status, Kind = kind, WorkspaceId = workspace, Cwd = "/tmp/" + label };
            _agents.Add(agent);
            return agent;
        }
    }

    public Agent? Find(string target)
    {
        lock (_lock)
        {
            return _agents.FirstOrDefault(a => a.Name == target || a.PaneId == target);
        }
    }

    public IReadOnlyList<string[]> CallsStartingWith(params string[] prefix) =>
        Calls.Where(c => StripSession(c).Take(prefix.Length).SequenceEqual(prefix)).ToList();

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Enqueue(arguments.ToArray());
        if (CommandDelay > TimeSpan.Zero)
        {
            await Task.Delay(CommandDelay, cancellationToken);
        }

        var a = StripSession(arguments.ToArray());
        var verb = a.Length >= 2 ? a[0] + " " + a[1] : string.Join(' ', a);
        switch (verb)
        {
            case "agent list":
                lock (_lock)
                {
                    return Ok(new { type = "agent_list", agents = _agents.Select(ToJson).ToArray() });
                }

            case "workspace list":
                lock (_lock)
                {
                    return Ok(new { type = "workspace_list", workspaces = _workspaces.Select(w => new { workspace_id = w.Key, label = w.Value }).ToArray() });
                }

            case "agent get":
                return Find(a[2]) is { } found ? Ok(new { type = "agent_info", agent = ToJson(found) }) : NotFound(a[2]);

            case "agent read":
                return Find(a[2]) is null ? NotFound(a[2]) : new ProcessResult(0, Screen, "");

            case "agent prompt":
            {
                if (PromptError is { } error)
                {
                    return Error(error, "simulated " + error);
                }

                if (Find(a[2]) is not { } agent)
                {
                    return NotFound(a[2]);
                }

                agent.Status = "working";
                if (a.Contains("--wait"))
                {
                    await Task.Delay(PromptDelay, cancellationToken);
                }

                agent.Status = StatusAfterPrompt;
                return Ok(new { type = "agent_prompted", agent = ToJson(agent) });
            }

            case "agent wait":
                return Find(a[2]) is { } waited ? Ok(new { type = "agent_info", agent = ToJson(waited) }) : NotFound(a[2]);

            case "agent send-keys":
                return Find(a[2]) is null ? NotFound(a[2]) : Ok(new { type = "ok" });

            case "agent rename":
            {
                if (Find(a[2]) is not { } agent)
                {
                    return NotFound(a[2]);
                }

                agent.Name = a[3];
                return Ok(new { type = "ok" });
            }

            case "workspace create":
            {
                var cwd = a[Array.IndexOf(a, "--cwd") + 1];
                var label = a[Array.IndexOf(a, "--label") + 1];
                string workspace;
                lock (_lock)
                {
                    workspace = "w" + _nextWorkspace++.ToString(CultureInfo.InvariantCulture);
                    _workspaces[workspace] = label;
                }

                return Ok(new
                {
                    type = "workspace_created",
                    workspace = new { workspace_id = workspace, label },
                    root_pane = new { pane_id = workspace + ":p1", cwd },
                });
            }

            case "workspace close":
                lock (_lock)
                {
                    _workspaces.Remove(a[2]);
                }

                return Ok(new { type = "ok" });

            case "agent start":
            {
                var name = a[2];
                var kind = a[Array.IndexOf(a, "--kind") + 1];
                var pane = a[Array.IndexOf(a, "--pane") + 1];
                lock (_lock)
                {
                    _agents.Add(new Agent { PaneId = pane, Name = name, Kind = kind, Status = StartBlocked ? "blocked" : "idle", WorkspaceId = pane.Split(':')[0] });
                }

                return StartBlocked ? Error("agent_not_ready", $"agent {name} is blocked during startup") : Ok(new { type = "agent_started" });
            }

            case "pane run":
            {
                var pane = a[2];
                lock (_lock)
                {
                    _agents.Add(new Agent { PaneId = pane, Name = null, Kind = "claude", Status = StartBlocked ? "blocked" : "idle", WorkspaceId = pane.Split(':')[0] });
                }

                // 和真实 herdr 一致：pane run 成功时没有任何输出。
                return new ProcessResult(0, "", "");
            }

            default:
                return Error("unknown_command", "fake herdr does not support: " + verb);
        }
    }

    internal static string[] StripSession(string[] args) =>
        args.Length >= 2 && args[0] == "--session" ? args[2..] : args;

    private object ToJson(Agent agent)
    {
        string? label;
        lock (_lock)
        {
            label = _workspaces.GetValueOrDefault(agent.WorkspaceId);
        }

        return new
        {
            agent = agent.Kind,
            agent_status = agent.Status,
            name = agent.Name,
            pane_id = agent.PaneId,
            workspace_id = agent.WorkspaceId,
            cwd = agent.Cwd,
            terminal_title_stripped = agent.Title,
        };
    }

    private static ProcessResult Ok(object result) =>
        new(0, JsonSerializer.Serialize(new { id = "cli:fake", result }), "");

    private static ProcessResult Error(string code, string message) =>
        new(1, "", JsonSerializer.Serialize(new { error = new { code, message }, id = "cli:fake" }));

    private static ProcessResult NotFound(string target) => Error("agent_not_found", $"agent target {target} not found");
}
