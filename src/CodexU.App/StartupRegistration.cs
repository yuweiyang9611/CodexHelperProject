using System.IO;
using Microsoft.Win32;

namespace CodexU.App;

internal static class StartupRegistration
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "codexU";

    public static bool IsEnabledForCurrentExecutable()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = Convert.ToString(key?.GetValue(ValueName))?.Trim();
        return string.Equals(command, $"\"{executable}\"", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的开机启动注册表项");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            if (key.GetValue(ValueName) is not null)
            {
                throw new InvalidOperationException("无法移除 codexU 的开机启动注册项");
            }

            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("无法确定 codexU 可执行文件路径，未启用开机启动");
        }

        var command = $"\"{executable}\"";
        key.SetValue(ValueName, command, RegistryValueKind.String);
        if (!string.Equals(Convert.ToString(key.GetValue(ValueName)), command, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("开机启动注册项写入后校验失败");
        }
    }
}
