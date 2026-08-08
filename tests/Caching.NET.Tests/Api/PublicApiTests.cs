using System.Reflection;

namespace Caching.NET.Tests.Api;

/// <summary>
/// Guards the public API against accidental change. Any addition, removal or signature edit fails
/// this test until <c>PublicApi.approved.txt</c> is updated deliberately, which turns a silent
/// binary-compatibility break into a reviewable diff.
/// </summary>
/// <remarks>
/// To accept an intended change: run with <c>CACHINGNET_APPROVE_API=1</c>, which rewrites the
/// approved file, then review that diff as part of the change.
/// </remarks>
public class PublicApiTests
{
    private const string ApprovalVariable = "CACHINGNET_APPROVE_API";

    [Fact]
    public void PublicSurface_MatchesTheApprovedBaseline()
    {
        var actual = Normalize(PublicApiSurface.Render(typeof(ICacheProvider).Assembly));
        var approvedPath = ApprovedFilePath();

        if (Environment.GetEnvironmentVariable(ApprovalVariable) == "1")
        {
            File.WriteAllText(approvedPath, actual);
        }

        Assert.True(File.Exists(approvedPath), $"Approved API file is missing: {approvedPath}");

        var approved = Normalize(File.ReadAllText(approvedPath));

        if (approved == actual)
        {
            return;
        }

        var approvedLines = approved.Split('\n');
        var actualLines = actual.Split('\n');
        var added = actualLines.Except(approvedLines, StringComparer.Ordinal).ToArray();
        var removed = approvedLines.Except(actualLines, StringComparer.Ordinal).ToArray();

        Assert.Fail(
            "The public API surface changed.\n\n"
            + $"Removed (breaking for consumers):\n{Render(removed)}\n"
            + $"Added:\n{Render(added)}\n"
            + $"If the change is intended, re-run with {ApprovalVariable}=1 and review the diff to "
            + "PublicApi.approved.txt as part of the change.");
    }

    [Fact]
    public void NoInternalNamespaceIsExported()
    {
        var leaked = typeof(ICacheProvider).Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace?.Contains(".Internal", StringComparison.Ordinal) == true)
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(leaked);
    }

    [Theory]
    [InlineData("ZiggyCreatures")]
    [InlineData("StackExchange")]
    [InlineData("Microsoft.Extensions.Caching.Memory")]
    public void ApprovedApi_NamesNoImplementationDetail(string bannedNamespace)
    {
        var approved = File.ReadAllText(ApprovedFilePath());

        Assert.DoesNotContain(
            bannedNamespace,
            approved,
            StringComparison.Ordinal);
    }

    // Every namespace that is an implementation detail of Caching.NET rather than part of its API.
    private static readonly string[] BannedNamespaces =
    [
        "ZiggyCreatures",
        "StackExchange",
        "Microsoft.Extensions.Caching.Memory"
    ];

    [Fact]
    public void NoEngineTypeAppearsInAPublicSignature()
    {
        // The design rule: the engine is an implementation detail. Caching.NET's own contract types
        // are the API, so any engine type in a public signature is a leak.
        //
        // Matched on Namespace, never FullName: FullName is null for a constructed generic that
        // contains a type parameter -- exactly the shape of the engine types this package wraps, such
        // as FusionCacheFactoryExecutionContext<TValue>. Reading FullName here let a real leak of that
        // shape pass this test and render namespace-stripped into the approved baseline.
        var leaked = typeof(ICacheProvider).Assembly
            .GetExportedTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(SignatureTypes)
            .Where(t => BannedNamespaces.Any(banned => t.Namespace?.StartsWith(banned, StringComparison.Ordinal) == true))
            .Select(t => $"{t.Namespace}.{t.Name}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(leaked);
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member)
    {
        IEnumerable<Type> types = member switch
        {
            MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
            ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
            PropertyInfo property => [property.PropertyType],
            FieldInfo field => [field.FieldType],
            _ => []
        };

        return types.SelectMany(Expand);
    }

    // Recursive, not one level deep: an engine type can hide arbitrarily far inside a signature, as
    // in ValueTask<MaybeValue<T>> or IReadOnlyList<FusionCacheEntryOptions>.
    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in Expand(element))
            {
                yield return nested;
            }
        }

        if (type.IsGenericType)
        {
            foreach (var nested in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return nested;
            }
        }
    }

    private static string Render(string[] lines)
        => lines.Length == 0 ? "  (none)" : string.Join("\n", lines.Where(l => l.Length > 0).Select(l => "  " + l));

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    // The approved file lives next to the test source, not in the output directory, so an approval
    // run updates the file that is actually committed.
    private static string ApprovedFilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Caching.NET.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "Api", "PublicApi.approved.txt");
    }
}
