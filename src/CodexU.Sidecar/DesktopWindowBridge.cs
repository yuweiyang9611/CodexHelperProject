using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexU.Sidecar;

/// <summary>Private child-process protocol. No HWND or native operations are exposed to Vue.</summary>
internal static class DesktopWindowBridge
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows() || !int.TryParse(Environment.GetEnvironmentVariable("CODEXU_PARENT_PID"), out var owner)) return 2;
        nint window = 0, originalParent = 0, originalStyle = 0;
        bool registered = false;
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (line.Length > 4096) return 2;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var action = root.GetProperty("action").GetString();
                    if (action == "shutdown") break;
                    if (action == "register")
                    {
                        if (registered) throw new InvalidOperationException("Window already registered.");
                        window = (nint)long.Parse(root.GetProperty("handle").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        VerifyOwner(window, owner);
                        originalParent = GetParent(window);
                        originalStyle = GetWindowLongPtrW(window, -16);
                        registered = true;
                    }
                    if (!registered) throw new InvalidOperationException("No registered window.");
                    VerifyOwner(window, owner);
                    if (action is "register" or "attach")
                    {
                        var host = FindDesktopHost();
                        if (host == 0) throw new InvalidOperationException("未找到可验证的 Explorer 桌面宿主。");
                        // SetParent may reset cross-process DPI awareness. The desktop
                        // window is deliberately in its own Electron process, never the
                        // main application's process, so that reset is contained here.
                        if (GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(window)) < 0
                            || GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(host)) < 0)
                            throw new InvalidOperationException("无法确认桌面 DPI 模式，已停止附着。");
                        GetWindowRect(window, out var rectangle);
                        SetWindowLongPtrW(window, -16, (nint)(((long)originalStyle & ~0x80000000L) | 0x40000000L));
                        Marshal.SetLastPInvokeError(0);
                        var prior = SetParent(window, host);
                        if (prior == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                        if (GetParent(window) != host) throw new InvalidOperationException("桌面窗口附着校验失败。");
                        var point = new Point { X = rectangle.Left, Y = rectangle.Top };
                        MapWindowPoints(0, host, ref point, 1);
                        SetWindowPos(window, 0, point.X, point.Y, rectangle.Right - rectangle.Left,
                            rectangle.Bottom - rectangle.Top, 0x0010 | 0x0020 | 0x0004);
                    }
                    else if (action != "status") throw new InvalidOperationException("Unsupported desktop operation.");
                    var attached = IsWindow(GetParent(window)) && IsExplorer(GetParent(window));
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, attached }));
                    await Console.Out.FlushAsync();
                }
                catch (Exception e)
                {
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { ok = false, attached = false, error = e.Message }));
                    await Console.Out.FlushAsync();
                }
            }
        }
        finally
        {
            if (registered && IsWindow(window))
            {
                GetWindowThreadProcessId(window, out var actual);
                if (actual == owner)
                {
                    SetParent(window, originalParent);
                    SetWindowLongPtrW(window, -16, originalStyle);
                }
            }
        }
        return 0;
    }

    private static void VerifyOwner(nint window, int expected)
    {
        if (!IsWindow(window)) throw new InvalidOperationException("Desktop window no longer exists.");
        GetWindowThreadProcessId(window, out var actual);
        if (actual != expected) throw new InvalidOperationException("Desktop window belongs to another process.");
    }
    private static bool IsExplorer(nint handle)
    {
        if (handle == 0) return false;
        GetWindowThreadProcessId(handle, out var pid);
        try { return string.Equals(Process.GetProcessById((int)pid).ProcessName, "explorer", StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
    private static nint FindDesktopHost()
    {
        var progman = FindWindowW("Progman", null);
        if (!IsExplorer(progman)) return 0;
        SendMessageTimeoutW(progman, 0x052C, 0, 0, 2, 1000, out _);
        nint found = 0;
        EnumWindows((window, _) =>
        {
            if (FindWindowExW(window, 0, "SHELLDLL_DefView", null) != 0)
            {
                var candidate = FindWindowExW(0, window, "WorkerW", null);
                if (IsExplorer(candidate)) { found = candidate; return false; }
            }
            return true;
        }, 0);
        if (found == 0)
        {
            var child = FindWindowExW(progman, 0, "WorkerW", null);
            if (IsExplorer(child)) found = child;
        }
        return found;
    }
    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint window, nint parent);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(nint from, nint to, ref Point point, uint count);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint window, uint message, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
}
