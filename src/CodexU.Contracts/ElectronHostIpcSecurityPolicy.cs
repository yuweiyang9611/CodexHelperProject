using System.Collections.Frozen;

namespace CodexU.Contracts;

/// <summary>
/// IPC methods reserved for the trusted Electron main process. These methods
/// are deliberately separate from <see cref="IpcSecurityPolicy"/>, whose
/// allow-list is exposed to renderer and WPF requests.
/// </summary>
public static class ElectronHostIpcSecurityPolicy
{
    private static readonly FrozenSet<string> AllowedMethods = FrozenSet.Create(
        StringComparer.Ordinal,
        "settings.reconcileStartupRegistration");

    public static IReadOnlyCollection<string> AllowedMethodNames => AllowedMethods;

    public static bool IsAllowedMethod(string method) => AllowedMethods.Contains(method);
}
