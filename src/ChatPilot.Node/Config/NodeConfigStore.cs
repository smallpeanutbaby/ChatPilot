using System.Text.Json;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Config;

/// <summary>读写节点配置文件。</summary>
public static class NodeConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>默认配置文件路径：<c>~/.chatpilot/node.json</c>，可以用环境变量 <c>CHATPILOT_NODE_CONFIG</c> 覆盖。</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("CHATPILOT_NODE_CONFIG") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chatpilot", "node.json");

    /// <summary>读取并校验配置。</summary>
    /// <exception cref="FileNotFoundException">还没配对。</exception>
    /// <exception cref="InvalidDataException">配置有问题，异常消息里列出了所有问题。</exception>
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
    public static void Save(string path, NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SecureFile.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
    }
}
