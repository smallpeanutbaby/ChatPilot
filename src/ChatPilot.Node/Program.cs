using ChatPilot.Node.Cli;

namespace ChatPilot.Node;

/// <summary>节点入口。不用顶级语句，避免和服务器的 Program 类在测试项目里重名。</summary>
internal static class NodeProgram
{
    private static Task<int> Main(string[] args) => NodeCli.RunAsync(args);
}
