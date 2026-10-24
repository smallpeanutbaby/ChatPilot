using System.Text;
using ChatPilot.Node.Herdr;
using ChatPilot.Shared;

namespace ChatPilot.Node.Execution;

/// <summary>git 不可用、目录不是仓库、或者 git 返回错误。</summary>
/// <param name="message">给用户看的原因。</param>
public sealed class GitException(string message) : Exception(message);

/// <summary>
/// <c>/diff</c>：在 agent 的工作目录里看 git 改动。只运行固定的只读命令，参数按数组传、不经过 shell。
/// </summary>
/// <remarks>
/// 仓库自己的配置可能让 git 运行别的程序，能关的都关掉：外部 diff 程序和文本转换（<c>--no-ext-diff</c>、<c>--no-textconv</c>）、
/// fsmonitor 钩子（<c>core.fsmonitor=false</c>）、验证提交签名的 gpg 程序（<c>log.showSignature</c> 配 <c>gpg.program</c>，用 <c>--no-show-signature</c> 关掉）、
/// 子模块（<c>diff.submodule=diff</c> 会让 git 在子模块里再跑一次不带上面这些开关的 diff，用 <c>--submodule=short</c> 关掉；
/// <c>--ignore-submodules=dirty</c> 不进子模块检查有没有没提交的改动，git 就不用在子模块里起进程，子模块里的改动因此不显示，换了提交仍会显示）。
/// <c>--no-optional-locks</c> 不让 <c>git status</c> 顺手改写索引，<c>--literal-pathspecs</c> 让文件参数按字面解释。
/// 仓库里配置的 clean 过滤器（<c>filter.*.clean</c> / <c>filter.*.process</c>）git 没有开关能关，<c>status</c> 和 <c>diff</c>
/// 仍会运行它们，和你自己在这个目录里运行 <c>git status</c> 一样；这些配置只能由能写这台电脑的人设置，服务器改不了。
/// </remarks>
/// <param name="runner">运行 git 进程用的执行器（它限制读多少输出，超过时标记截断）。</param>
/// <param name="gitPath">git 可执行文件（节点配置的 <c>gitPath</c>）。</param>
/// <param name="timeout">每次运行 git 的超时。</param>
public sealed class GitInspector(IProcessRunner runner, string gitPath, TimeSpan timeout)
{
    /// <summary>返回的文字上限（和 /read 一样）。</summary>
    internal const int MaxChars = 12000;

    /// <summary>概要里最多列这么多个改动文件，其余只报个数。</summary>
    private const int MaxStatusLines = 60;

    /// <summary>每次 <c>git diff</c> 都带的参数：不运行外部 diff 程序和文本转换、不上色、子模块只给一行概要、不进子模块检查改动。</summary>
    private static readonly string[] DiffOptions = ["--no-ext-diff", "--no-textconv", "--no-color", "--submodule=short", "--ignore-submodules=dirty"];

    /// <summary>改动概要：分支和改了哪些文件、改动行数统计、最近几次提交。</summary>
    /// <param name="directory">agent 的工作目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>给用户看的概要文字，超长时截断。</returns>
    /// <exception cref="GitException">git 不可用或目录不是仓库。</exception>
    public async Task<string> SummaryAsync(string directory, CancellationToken cancellationToken)
    {
        var root = await RepositoryRootAsync(directory, cancellationToken).ConfigureAwait(false);
        var status = await RunAsync(directory, ["status", "--short", "--branch", "--untracked-files=normal", "--ignore-submodules=dirty"], cancellationToken)
            .ConfigureAwait(false);
        var stat = await DiffStatAsync(directory, cancellationToken).ConfigureAwait(false);
        var log = await TryRunAsync(
            directory, ["log", "-3", "--no-show-signature", "--no-color", "--date=format:%m-%d %H:%M", "--format=%h %ad %s"], cancellationToken)
            .ConfigureAwait(false);

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
    /// <param name="directory">agent 的工作目录。</param>
    /// <param name="path">相对工作目录的文件路径，不能有 <c>..</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>diff 文字，超长时截断；没有 diff 时是一句说明（未跟踪的新文件，或者没有改动、路径不对）。</returns>
    /// <exception cref="GitException">git 不可用、目录不是仓库或路径不合法。</exception>
    public async Task<string> FileDiffAsync(string directory, string path, CancellationToken cancellationToken)
    {
        if (!Validation.IsRepoRelativePath(path))
        {
            throw new GitException("文件路径要写相对 agent 工作目录的路径，例如 src/app.ts，不能有 ..");
        }

        await RepositoryRootAsync(directory, cancellationToken).ConfigureAwait(false);
        var diff = await InvokeAsync(directory, ["diff", "HEAD", .. DiffOptions, "--", path], cancellationToken).ConfigureAwait(false);
        if (diff.ExitCode != 0 && !diff.Truncated)
        {
            // 仓库还没有任何提交时 diff HEAD 会失败，只看暂存区。
            diff = await InvokeAsync(directory, ["diff", "--cached", .. DiffOptions, "--", path], cancellationToken).ConfigureAwait(false);
            if (diff.ExitCode != 0 && !diff.Truncated)
            {
                throw Failed(diff);
            }
        }

        if (!string.IsNullOrWhiteSpace(diff.StandardOutput))
        {
            return Clip(diff.StandardOutput.TrimEnd(), diff.Truncated);
        }

        var status = await RunAsync(directory, ["status", "--short", "--untracked-files=all", "--ignore-submodules=dirty", "--", path], cancellationToken)
            .ConfigureAwait(false);
        return status.TrimStart().StartsWith("??", StringComparison.Ordinal)
            ? "这是还没纳入 git 的新文件，git diff 看不到它的内容"
            : "这个文件相对上次提交没有改动（或者路径不对）";
    }

    /// <summary>已暂存和未暂存的改动加在一起的行数统计；仓库还没有任何提交时只统计暂存区。</summary>
    /// <param name="directory">仓库里的目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns><c>git diff --stat</c> 的输出；没有改动时为空。</returns>
    /// <exception cref="GitException">git 不可用或出错。</exception>
    private async Task<string> DiffStatAsync(string directory, CancellationToken cancellationToken) =>
        await TryRunAsync(directory, ["diff", "HEAD", "--stat=72", .. DiffOptions], cancellationToken).ConfigureAwait(false)
        ?? await RunAsync(directory, ["diff", "--cached", "--stat=72", .. DiffOptions], cancellationToken).ConfigureAwait(false);

    /// <summary>确认目录存在并且在 git 仓库里，取仓库根目录。</summary>
    /// <param name="directory">agent 的工作目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>仓库根目录。</returns>
    /// <exception cref="GitException">目录不存在、不是 git 仓库，或者 git 不可用。</exception>
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

    /// <summary>运行 git；退出码不为 0 时返回 null（例如仓库还没有提交时 <c>diff HEAD</c> 会失败）。输出太长被截断时返回读到的部分。</summary>
    /// <param name="directory">运行 git 的目录。</param>
    /// <param name="args">git 子命令和参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标准输出（可能被截断）；退出码不为 0 时为 null。</returns>
    /// <exception cref="GitException">找不到 git 或运行超时。</exception>
    private async Task<string?> TryRunAsync(string directory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(directory, args, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 || result.Truncated ? result.StandardOutput : null;
    }

    /// <summary>运行 git，失败时把 git 的错误输出（截短）交给用户看。输出太长被截断时返回读到的部分。</summary>
    /// <param name="directory">运行 git 的目录。</param>
    /// <param name="args">git 子命令和参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标准输出（可能被截断）。</returns>
    /// <exception cref="GitException">找不到 git、运行超时，或者退出码不为 0。</exception>
    private async Task<string> RunAsync(string directory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(directory, args, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 || result.Truncated ? result.StandardOutput : throw Failed(result);
    }

    /// <summary>git 失败了：把它的错误输出（截短）交给用户看。</summary>
    /// <param name="result">退出码不为 0 的运行结果。</param>
    /// <returns>要抛出的异常。</returns>
    private static GitException Failed(ProcessResult result) =>
        new("git 出错：" + Shared.Text.TextChunker.Truncate(result.StandardError.Trim(), 300));

    /// <summary>加上固定的全局参数（不拿可选锁、路径按字面解释、关 fsmonitor、不转义非 ASCII 路径、不上色），在 <paramref name="directory"/> 里运行 git。</summary>
    /// <param name="directory">运行 git 的目录（用 <c>-C</c> 传入）。</param>
    /// <param name="args">git 子命令和参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>git 的运行结果，退出码由调用方判断。</returns>
    /// <exception cref="GitException">找不到 git 或运行超时。</exception>
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

    /// <summary>超过 <see cref="MaxChars"/> 时截断，并在结尾注明还剩多少字；git 的输出本身太长、没有读完时只说截断了（不知道还剩多少）。</summary>
    /// <param name="text">要返回给用户的文字。</param>
    /// <param name="incomplete">文字是 git 输出的前一部分，后面还有没读的。</param>
    /// <returns>不超过上限的文字。</returns>
    private static string Clip(string text, bool incomplete = false)
    {
        if (!incomplete && text.Length <= MaxChars)
        {
            return text;
        }

        var kept = text[..Math.Min(text.Length, MaxChars - 40)].TrimEnd();
        return incomplete ? kept + "\n…（输出太长，后面的截断了）" : kept + $"\n…（还有 {text.Length - MaxChars + 40} 字，截断了）";
    }
}
