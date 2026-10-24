using System.Text.Json;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Config;

/// <summary>读写节点配置文件。</summary>
public static class NodeConfigStore
{
    /// <summary>配置文件的 JSON 设置：驼峰命名、属性名不区分大小写、带缩进（方便用户手改）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>默认配置文件路径：<c>~/.chatpilot/node.json</c>，可以用环境变量 <c>CHATPILOT_NODE_CONFIG</c> 覆盖。</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("CHATPILOT_NODE_CONFIG") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chatpilot", "node.json");

    /// <summary>
    /// 节点的单实例锁文件：和配置同名、扩展名换成 <c>.lock</c>（例如 <c>node.lock</c>），
    /// 和 <c>node.tasks.json</c>、<c>node.queue.json</c> 这些状态文件按同样的规则命名，用同一份状态文件的节点也就用同一把锁。
    /// </summary>
    /// <param name="configPath">配置文件路径。</param>
    /// <returns>锁文件的完整路径。</returns>
    public static string LockPathFor(string configPath) => Path.ChangeExtension(Path.GetFullPath(configPath), ".lock");

    /// <summary>读取并校验配置。</summary>
    /// <param name="path">配置文件路径。</param>
    /// <returns>通过 <see cref="NodeConfig.Validate"/> 校验的配置。</returns>
    /// <exception cref="FileNotFoundException">还没配对。</exception>
    /// <exception cref="InvalidDataException">配置有问题，异常消息里列出了所有问题。</exception>
    /// <exception cref="JsonException">配置文件不是合法的 JSON（例如手改坏了）。</exception>
    public static NodeConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到节点配置 {path}，请先运行 pair 配对", path);
        }

        var config = JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"节点配置 {path} 是空的");
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"节点配置 {path} 有问题：\n- " + string.Join("\n- ", errors));
        }

        return config;
    }

    /// <summary>保存配置（权限 600，原子替换）。</summary>
    /// <param name="path">配置文件路径。</param>
    /// <param name="config">要保存的配置；不在这里校验，调用方先调 <see cref="NodeConfig.Validate"/>。</param>
    public static void Save(string path, NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SecureFile.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
    }
}
