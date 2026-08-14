using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace CodexU.Contracts;

public static class IpcSecurityPolicy
{
    public const string TrustedHost = "app.codexu.local";

    // Frozen rather than a HashSet handed out through IReadOnlyCollection: that
    // interface is a compile-time convenience, not a boundary, so a caller could
    // downcast the exposed instance and mutate the live policy that
    // IsAllowedMethod reads. A FrozenSet cannot be downcast to HashSet and
    // refuses mutation, and it makes the dispatch-path lookup faster besides.
    private static readonly FrozenSet<string> AllowedMethods = FrozenSet.Create(
        StringComparer.Ordinal,
        "app.initialize",
        "app.ready",
        "usage.getSnapshot",
        "usage.refresh",
        "usage.getCombined",
        "runtime.select",
        "settings.get",
        "settings.update",
        "statusStrip.getState",
        "statusStrip.preview",
        "statusStrip.recover",
        "rates.getCatalog",
        "rates.export",
        "rates.import",
        "rates.reset",
        "todos.list",
        "todos.add",
        "todos.update",
        "todos.toggle",
        "todos.delete",
        "todos.clearCompleted",
        "update.check",
        "update.openRelease",
        "data.exportAggregates",
        "data.backup",
        "data.restore",
        "diagnostics.export",
        "diagnostics.rebuildIndex",
        "window.toggleCompact",
        "window.setAlwaysOnTop",
        "window.show",
        "window.hide");

    /// <summary>
    /// The complete IPC surface. The host's dispatch switch must handle exactly
    /// these methods; <c>IpcSecurityPolicyParityTests</c> enforces that.
    /// </summary>
    public static IReadOnlyCollection<string> AllowedMethodNames => AllowedMethods;

    public static bool IsTrustedSource(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, TrustedHost, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsValidRequest([NotNullWhen(true)] IpcRequest? request) =>
        request is
        {
            Version: 1,
            Type: "request",
            Id.Length: > 0 and <= 128,
            Method.Length: > 0 and <= 128
        };

    public static bool IsAllowedMethod(string method) => AllowedMethods.Contains(method);
}
