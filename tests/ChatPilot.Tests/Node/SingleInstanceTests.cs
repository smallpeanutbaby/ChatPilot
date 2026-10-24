using ChatPilot.Node.Cli;
using ChatPilot.Node.Config;
using ChatPilot.Node.Execution;
using ChatPilot.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatPilot.Tests.Node;

/// <summary>
/// 同一份配置同时只能运行一个节点（审查 #10）：两个节点都会读 <c>node.queue.json</c>、各自把排队的指令再发一遍、互相覆盖状态文件。
/// 升级重启时新节点等旧节点放开锁再接手。
/// </summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Lock_is_exclusive_until_released()
    {
        var path = _temp.Combine("node.lock");
        using (var first = ExclusiveFileLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(ExclusiveFileLock.TryAcquire(path));
        }

        using var again = ExclusiveFileLock.TryAcquire(path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Waiting_for_the_lock_takes_over_once_the_old_holder_exits()
    {
        // 升级重启：旧节点还差一点才退出，新节点等它放开锁后接手。
        var path = _temp.Combine("node.lock");
        var old = ExclusiveFileLock.TryAcquire(path)!;
        var waiting = ExclusiveFileLock.AcquireAsync(path, TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(400);
        Assert.False(waiting.IsCompleted);

        old.Dispose();
        using var taken = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(taken);
    }

    [Fact]
    public async Task Waiting_for_the_lock_gives_up_after_the_limit_or_when_stopped()
    {
        var path = _temp.Combine("node.lock");
        using var held = ExclusiveFileLock.TryAcquire(path);
        Assert.Null(await ExclusiveFileLock.AcquireAsync(path, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        // 等的时候收到退出信号：不启动，也不报错。
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var configPath = _temp.Combine("node.json");
        using var instance = await NodeCli.AcquireInstanceLockAsync(configPath, TimeSpan.FromSeconds(20), NullLogger.Instance, stopping.Token);
        Assert.Null(instance);
    }

    [Fact]
    public void Lock_follows_the_config_like_the_other_state_files()
    {
        var config = _temp.Combine("node.json");
        Assert.Equal(_temp.Combine("node.lock"), NodeConfigStore.LockPathFor(config));
        Assert.Equal(Path.GetDirectoryName(PromptQueue.PathFor(config)), Path.GetDirectoryName(NodeConfigStore.LockPathFor(config)));
        Assert.NotEqual(NodeConfigStore.LockPathFor(config), NodeConfigStore.LockPathFor(_temp.Combine("other.json")));
    }

    [Fact]
    public async Task Second_node_on_the_same_config_exits_without_starting()
    {
        // 第二个节点等不到锁就以 0 退出（开机自启工具不会反复拉起它），根本不会去读排队的指令、连服务器。
        // 要是它往下走了，会一直重连 127.0.0.1:9，测试就会超时。
        var configPath = _temp.Combine("node.json");
        NodeConfigStore.Save(configPath, new NodeConfig
        {
            ServerUrl = "http://127.0.0.1:9",
            DeviceId = "device-1",
            DeviceName = "mac",
            Token = "token",
            AllowedRoots = [_temp.Path],
        });
        File.WriteAllText(PromptQueue.PathFor(configPath), "这不是 JSON：第二个节点不该读它");

        using var running = ExclusiveFileLock.TryAcquire(NodeConfigStore.LockPathFor(configPath));
        Assert.NotNull(running);
        var previous = NodeCli.InstanceLockWait;
        NodeCli.InstanceLockWait = TimeSpan.FromMilliseconds(300);
        try
        {
            var exit = await NodeCli.RunAsync(["run", "--config", configPath]).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, exit);
        }
        finally
        {
            NodeCli.InstanceLockWait = previous;
        }

        Assert.Equal("这不是 JSON：第二个节点不该读它", File.ReadAllText(PromptQueue.PathFor(configPath)));
    }
}
