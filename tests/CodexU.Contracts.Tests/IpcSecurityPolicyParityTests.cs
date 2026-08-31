using System.Text.RegularExpressions;
using CodexU.Contracts;

namespace CodexU.Contracts.Tests;

/// <summary>
/// During the Electron migration the IPC surface is declared in three places:
/// the allow-list in <see cref="IpcSecurityPolicy"/>, the shared application
/// dispatcher, and the still-shipping WPF dispatcher. Adding a method to only
/// some of them is silent — an
/// allow-listed method with no case throws NotSupportedException at runtime, and
/// a case with no allow-list entry is unreachable. These tests make it loud.
/// </summary>
public sealed class IpcSecurityPolicyParityTests
{
    private const string ApplicationDispatchRelativePath =
        "src/CodexU.Application/IpcDispatcher.cs";

    private const string WpfDispatchRelativePath =
        "src/CodexU.App/MainWindow.Ipc.cs";

    [Fact]
    public void DispatchSwitchHandlesExactlyTheAllowedMethods()
    {
        var rendererMethods = IpcSecurityPolicy.AllowedMethodNames.ToHashSet(StringComparer.Ordinal);
        var electronHostMethods = ElectronHostIpcSecurityPolicy.AllowedMethodNames
            .ToHashSet(StringComparer.Ordinal);

        AssertDispatchMethods(WpfDispatchRelativePath, rendererMethods);
        AssertDispatchMethods(
            ApplicationDispatchRelativePath,
            rendererMethods.Concat(electronHostMethods).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void WpfDispatcherCannotReachElectronHostOnlyMethods()
    {
        var dispatched = ReadDispatchedMethods(WpfDispatchRelativePath);

        Assert.Empty(dispatched.Intersect(ElectronHostIpcSecurityPolicy.AllowedMethodNames));
        Assert.False(IpcSecurityPolicy.IsAllowedMethod("settings.reconcileStartupRegistration"));
        Assert.True(ElectronHostIpcSecurityPolicy.IsAllowedMethod(
            "settings.reconcileStartupRegistration"));
    }

    [Fact]
    public void CombinedRuntimeReadIsReachable()
    {
        // The parity test above would catch this too, but only as a set difference.
        // Named here so removing the entry fails with the reason rather than a diff:
        // without it the combined view's only data source is rejected at the boundary.
        Assert.True(IpcSecurityPolicy.IsAllowedMethod("usage.getCombined"));
    }

    [Fact]
    public void AllowedMethodsAreUniqueAndNamespaced()
    {
        var rendererAllowed = IpcSecurityPolicy.AllowedMethodNames;
        var hostAllowed = ElectronHostIpcSecurityPolicy.AllowedMethodNames;

        Assert.Equal(rendererAllowed.Count, rendererAllowed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(hostAllowed.Count, hostAllowed.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(rendererAllowed.Intersect(hostAllowed, StringComparer.Ordinal));
        Assert.All(
            rendererAllowed.Concat(hostAllowed),
            method => Assert.Matches(@"^[a-z][a-zA-Z]*\.[a-zA-Z]+$", method));
    }

    [Fact]
    public void ExposingTheAllowListCannotWidenThePolicy()
    {
        var exposed = IpcSecurityPolicy.AllowedMethodNames;

        // IReadOnlyCollection is a compile-time convenience, so the guarantee has
        // to come from the concrete type: a caller must not be able to downcast
        // and edit the set that IsAllowedMethod reads.
        Assert.IsNotType<HashSet<string>>(exposed);
        Assert.False(exposed is ISet<string> { IsReadOnly: false });

        var mutable = Assert.IsAssignableFrom<ICollection<string>>(exposed);
        Assert.True(mutable.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutable.Add("evil.method"));
        Assert.Throws<NotSupportedException>(() => mutable.Clear());

        Assert.False(IpcSecurityPolicy.IsAllowedMethod("evil.method"));
    }

    [Fact]
    public void EveryAllowedMethodPassesTheAllowListCheck()
    {
        Assert.All(IpcSecurityPolicy.AllowedMethodNames, method =>
            Assert.True(IpcSecurityPolicy.IsAllowedMethod(method)));
    }

    [Fact]
    public void HostCapabilityNamesMatchTheWebContract()
    {
        var contractNames = typeof(HostCapabilityNames)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToHashSet(StringComparer.Ordinal);
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src/CodexU.Web/src/hostCapabilities.ts"));
        var webNames = Regex.Matches(
                source,
                @"^\s+\w+:\s*'(?<capability>[^']+)',?\s*$",
                RegexOptions.Multiline)
            .Select(match => match.Groups["capability"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            contractNames.Order(StringComparer.Ordinal),
            webNames.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("usage.getsnapshot")]
    [InlineData("USAGE.GETSNAPSHOT")]
    [InlineData("usage.getSnapshot ")]
    [InlineData("")]
    public void AllowListMatchingIsExact(string method) =>
        Assert.False(IpcSecurityPolicy.IsAllowedMethod(method));

    private static HashSet<string> ReadDispatchedMethods(string dispatchRelativePath)
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), dispatchRelativePath));

        // Scoped to the single dispatch switch so unrelated switches added later
        // cannot silently widen what this test believes the IPC surface to be.
        var switchMatches = Regex.Matches(source, @"switch\s*\(\s*request\.Method\s*\)");
        Assert.True(
            switchMatches.Count == 1,
            $"Expected exactly one dispatch switch in {dispatchRelativePath}, found {switchMatches.Count}. "
            + "Update this test if the dispatch shape changed.");

        var start = switchMatches[0].Index;
        var end = source.IndexOf("default:", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Dispatch switch in {dispatchRelativePath} has no default arm.");

        var body = source[start..end];
        return Regex.Matches(body, """case\s+"(?<method>[^"]+)"\s*:""")
            .Select(match => match.Groups["method"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertDispatchMethods(
        string dispatchRelativePath,
        HashSet<string> allowed)
    {
        var dispatched = ReadDispatchedMethods(dispatchRelativePath);
        var missingCases = allowed.Except(dispatched).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var unreachableCases = dispatched.Except(allowed).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.True(
            missingCases.Length == 0,
            $"Allow-listed but not handled in {dispatchRelativePath}: {string.Join(", ", missingCases)}");
        Assert.True(
            unreachableCases.Length == 0,
            $"Handled in {dispatchRelativePath} but not allow-listed, so unreachable: "
            + string.Join(", ", unreachableCases));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexU.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
