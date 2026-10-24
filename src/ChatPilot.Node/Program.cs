using ChatPilot.Node.Cli;

namespace ChatPilot.Node;

/// <summary>节点入口。不用顶级语句，避免和服务器的 Program 类在测试项目里重名。</summary>
internal static class NodeProgram
{
    /// <summary>把命令行交给 <see cref="NodeCli.RunAsync"/>。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码，含义见 <see cref="NodeCli.RunAsync"/>。</returns>
    private static Task<int> Main(string[] args) => NodeCli.RunAsync(args);
}
