using System.Text;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared;

namespace ChatPilot.Node.Execution;

/// <summary>git 不可用、目录不是仓库、或者 git 返回错误。</summary>
public sealed class GitException(string message) : Exception(message);

/// <summary>
/// <c>/diff</c>：在 agent 的工作目录里看 git 改动。只运行固定的只读命令，参数按数组传、不经过 shell。
/// </summary>
/// <remarks>
/// 仓库自己的配置可能指定外部 diff 程序、文本转换、fsmonitor 钩子，能关的都关掉：
/// <c>--no-ext-diff</c>、<c>--no-textconv</c>、<c>core.fsmonitor=false</c>。<c>--no-optional-locks</c> 不让
/// <c>git status</c> 顺手改写索引，<c>--literal-pathspecs</c> 让文件参数按字面解释。
/// 仓库里配置的 clean 过滤器（<c>filter.*.clean</c> / <c>filter.*.process</c>）git 没有开关能关，<c>status</c> 和 <c>diff</c>
/// 仍会运行它们，和你自己在这个目录里运行 <c>git status</c> 一样；这些配置只能由能写这台电脑的人设置，服务器改不了。
/// </remarks>
public sealed class GitInspector(IProcessRunner runner, string gitPath, TimeSpan timeout)
{
    /// <summary>返回的文字上限（和 /read 一样）。</summary>
    internal const int MaxChars = 12000;

    private const int MaxStatusLines = 60;

    /// <summary>改动概要：分支和改了哪些文件、改动行数统计、最近几次提交。</summary>
    /// <exception cref="GitException">git 不可用或目录不是仓库。</exception>
    public async Task<string> SummaryAsync(string directory, CancellationToken cancellationToken)
    {
        var root = await RepositoryRootAsync(directory, cancellationToken).ConfigureAwait(false);
        var status = await RunAsync(directory, ["status", "--short", "--branch", "--untracked-files=normal"], cancellationToken).ConfigureAwait(false);
        var stat = await DiffStatAsync(directory, cancellationToken).ConfigureAwait(false);
        var log = await TryRunAsync(directory, ["log", "-3", "--no-color", "--date=format:%m-%d %H:%M", "--format=%h %ad %s"], cancellationToken).ConfigureAwait(false);

        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var branch = lines.Count > 0 && lines[0].StartsWith("## ", StringComparison.Ordinal) ? lines[0][3..] : null;
        var files = lines.Where(l => !l.StartsWith("## ", StringComparison.Ordinal)).ToList();

        var builder = new StringBuilder();
        builder.Append("仓库：").Append(root);
        if (branch is not null)
        {
            builder.Append("\n分支：").Append(branch);
        }

        builder.Append("\n\n");
        if (files.Count == 0)
        {
            builder.Append("工作区没有未提交的改动");
        }
        else
        {
            builder.Append("未提交的改动（M 修改、A 新增、D 删除、?? 未跟踪）：\n");
            builder.AppendJoin('\n', files.Take(MaxStatusLines));
            if (files.Count > MaxStatusLines)
            {
                builder.Append("\n…还有 ").Append(files.Count - MaxStatusLines).Append(" 个文件");
            }

            if (!string.IsNullOrWhiteSpace(stat))
            {
                builder.Append("\n\n").Append(stat.TrimEnd());
            }
        }

        if (!string.IsNullOrWhiteSpace(log))
        {
            builder.Append("\n\n最近的提交：\n").Append(log.TrimEnd());
        }

        return Clip(builder.ToString());
    }

    /// <summary>一个文件相对上次提交的 diff（包括已暂存和未暂存的改动）。</summary>
    /// <exception cref="GitException">git 不可用、目录不是仓库或路径不合法。</exception>
    public async Task<string> FileDiffAsync(string directory, string path, CancellationToken cancellationToken)
    {
        if (!Validation.IsRepoRelativePath(path))
        {
            throw new GitException("文件路径要写相对 agent 工作目录的路径，例如 src/app.ts，不能有 ..");
        }

        await RepositoryRootAsync(directory, cancellationToken).ConfigureAwait(false);
        var diff = await TryRunAsync(directory, ["diff", "HEAD", "--no-ext-diff", "--no-textconv", "--no-color", "--", path], cancellationToken).ConfigureAwait(false)
            ?? await RunAsync(directory, ["diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "--", path], cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(diff))
        {
            return Clip(diff.TrimEnd());
        }

        var status = await RunAsync(directory, ["status", "--short", "--untracked-files=all", "--", path], cancellationToken).ConfigureAwait(false);
        return status.TrimStart().StartsWith("??", StringComparison.Ordinal)
            ? "这是还没纳入 git 的新文件，git diff 看不到它的内容"
            : "这个文件相对上次提交没有改动（或者路径不对）";
    }

    /// <summary>已暂存和未暂存的改动加在一起的行数统计；仓库还没有任何提交时只统计暂存区。</summary>
    private async Task<string> DiffStatAsync(string directory, CancellationToken cancellationToken) =>
        await TryRunAsync(directory, ["diff", "HEAD", "--stat=72", "--no-ext-diff", "--no-textconv", "--no-color"], cancellationToken).ConfigureAwait(false)
        ?? await RunAsync(directory, ["diff", "--cached", "--stat=72", "--no-ext-diff", "--no-textconv", "--no-color"], cancellationToken).ConfigureAwait(false);

    private async Task<string> RepositoryRootAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            throw new GitException($"agent 的工作目录 {directory} 不存在");
        }

        var root = await TryRunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(root)
            ? throw new GitException($"{directory} 不是 git 仓库")
            : root.Trim();
    }

    /// <summary>运行 git；退出码不为 0 时返回 null（例如仓库还没有提交时 <c>diff HEAD</c> 会失败）。</summary>
    private async Task<string?> TryRunAsync(string directory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(directory, args, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    private async Task<string> RunAsync(string directory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(directory, args, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? result.StandardOutput
            : throw new GitException("git 出错：" + Shared.Text.TextChunker.Truncate(result.StandardError.Trim(), 300));
    }

    private async Task<ProcessResult> InvokeAsync(string directory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        List<string> all =
        [
            "--no-optional-locks", "--literal-pathspecs",
            "-c", "core.fsmonitor=false", "-c", "core.quotepath=false", "-c", "color.ui=never",
            "-C", directory,
        ];
        all.AddRange(args);
        try
        {
            return await runner.RunAsync(gitPath, all, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessStartException)
        {
            throw new GitException($"找不到 git（{gitPath}）。装好 git，或者在节点配置的 gitPath 里写 git 的绝对路径");
        }
        catch (TimeoutException)
        {
            throw new GitException($"git 运行超过 {timeout.TotalSeconds:0} 秒，已放弃");
        }
    }

    private static string Clip(string text) =>
        text.Length <= MaxChars ? text : text[..(MaxChars - 40)].TrimEnd() + $"\n…（还有 {text.Length - MaxChars + 40} 字，截断了）";
}
