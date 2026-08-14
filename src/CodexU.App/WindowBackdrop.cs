using System.Runtime.InteropServices;

namespace CodexU.App;

internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int MicaBackdrop = 2;

    public static void TryApply(IntPtr windowHandle, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        try
        {
            var darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(windowHandle, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));
            var backdrop = MicaBackdrop;
            DwmSetWindowAttribute(windowHandle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows builds use the solid fallback background.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows builds use the solid fallback background.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
