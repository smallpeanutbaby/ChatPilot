using System.Text.Json;
using ChatPilot.Shared.Security;

namespace ChatPilot.Node.Config;

/// <summary>读写节点配置文件。</summary>
public static class NodeConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("CHATPILOT_NODE_CONFIG") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chatpilot", "node.json");

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

    public static void Save(string path, NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SecureFile.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
    }
}
