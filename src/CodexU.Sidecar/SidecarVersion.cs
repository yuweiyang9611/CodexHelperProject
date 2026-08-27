using System.Reflection;

namespace CodexU.Sidecar;

internal static class SidecarVersion
{
    internal static string Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return Normalize(informationalVersion, assembly.GetName().Version);
    }

    internal static string Normalize(
        string? informationalVersion,
        Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var normalizedVersion = informationalVersion.Trim();
            var buildMetadataIndex = normalizedVersion.IndexOf('+');
            if (buildMetadataIndex >= 0)
            {
                normalizedVersion = normalizedVersion[..buildMetadataIndex];
            }

            if (!string.IsNullOrWhiteSpace(normalizedVersion))
            {
                return normalizedVersion;
            }
        }

        return assemblyVersion?.ToString(3) ?? "development";
    }
}
