namespace ChatPilot.Tests.Support;

/// <summary>
/// 用同名目录占住服务器的 state.json，让之后的原子替换必然失败，用来模拟磁盘写入失败。
/// （改目录权限模拟不了：程序写入前会把自己创建的目录设成 700。）
/// </summary>
internal sealed class StateFileBlocker : IAsyncDisposable
{
    private readonly string _path;
    private readonly string _backup;

    private StateFileBlocker(string path)
    {
        _path = path;
        _backup = path + ".bak";
    }

    /// <summary>
    /// 占住状态文件。服务器可能正好在写（例如设备刚上线时记录时间），所以失败时稍等重试。
    /// </summary>
    public static async Task<StateFileBlocker> BlockAsync(string statePath)
    {
        var blocker = new StateFileBlocker(statePath);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(statePath))
                {
                    File.Move(statePath, blocker._backup, overwrite: true);
                }

                Directory.CreateDirectory(statePath);
                return blocker;
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(20);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_path, recursive: true);
        if (File.Exists(_backup))
        {
            File.Move(_backup, _path);
        }

        return ValueTask.CompletedTask;
    }
}
