using System.Globalization;
using System.Text;
using ChatPilot.Shared;

namespace ChatPilot.Node.Execution;

/// <summary>根据目录生成 agent 名字和工作区标签。</summary>
public static class AgentNaming
{
    /// <summary>由目录名生成符合 herdr 要求的 agent 名字（小写字母开头，最长 32 位），并避开已有名字。</summary>
    /// <param name="directory">agent 的工作目录，取最后一级目录名。</param>
    /// <param name="existingNames">已经被占用的 agent 名字。</param>
    /// <returns>可以直接用的 agent 名字；目录名里没有可用字符时为 <c>agent</c>（重名时加序号）。</returns>
    public static string FromDirectory(string directory, IReadOnlySet<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var builder = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var baseName = builder.ToString().Trim('-', '_');
        if (baseName.Length == 0 || baseName[0] is < 'a' or > 'z')
        {
            baseName = baseName.Length == 0 ? "agent" : "a-" + baseName;
        }

        return MakeUnique(baseName, existingNames);
    }

    /// <summary>在名字后加 -2、-3… 直到不重复，总长度不超过 32。</summary>
    /// <param name="baseName">想用的名字，超过 32 位时先截短。</param>
    /// <param name="existingNames">已经被占用的 agent 名字。</param>
    /// <returns>不和已有名字重复的名字；本身不重复时原样（截短后）返回。</returns>
    public static string MakeUnique(string baseName, IReadOnlySet<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(existingNames);
        var trimmed = baseName.Length > 32 ? baseName[..32].TrimEnd('-', '_') : baseName;
        if (!existingNames.Contains(trimmed))
        {
            return trimmed;
        }

        for (var i = 2; ; i++)
        {
            var suffix = "-" + i.ToString(CultureInfo.InvariantCulture);
            var candidate = (trimmed.Length + suffix.Length > 32 ? trimmed[..(32 - suffix.Length)] : trimmed) + suffix;
            if (!existingNames.Contains(candidate) && Validation.IsAgentName(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>工作区标签：目录名去掉控制字符，最长 40 个字符，不以 <c>-</c> 开头。</summary>
    /// <param name="directory">工作区目录，取最后一级目录名。</param>
    /// <returns>清理后的标签；清理后为空时为 <c>workspace</c>。</returns>
    public static string WorkspaceLabel(string directory)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim().TrimStart('-');
        if (cleaned.Length == 0)
        {
            cleaned = "workspace";
        }

        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }
}
