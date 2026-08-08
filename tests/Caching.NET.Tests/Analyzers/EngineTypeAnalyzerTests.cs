using System.Collections.Immutable;
using Caching.NET.Analyzers;
using Caching.NET.Options;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Analyzers;

/// <summary>
/// The shipped analyzer, run against real compilations.
/// </summary>
/// <remarks>
/// <c>CACHENET001</c> is the compile-time half of Caching.NET's engine-hiding guarantee: it flags any
/// reference to a <c>ZiggyCreatures.Caching.Fusion</c> symbol in consumer code, so a source dependency
/// on the internal cache engine is caught at build time instead of surfacing later as a break when the
/// engine is swapped out.
/// </remarks>
public class EngineTypeAnalyzerTests
{
    [Fact]
    public async Task EngineTypeInConsumerCode_IsReported()
    {
        const string source = """
            using ZiggyCreatures.Caching.Fusion;

            public class Consumer
            {
                public void Use(IFusionCache cache) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 1);
    }

    /// <summary>
    /// <c>StackExchange.Redis</c> is a transitive dependency, not the engine. An application may
    /// legitimately use the Redis client directly for something that is not the cache — a rate
    /// limiter, a distributed lock, pub/sub — and flagging that would break the consumer's build under
    /// <c>TreatWarningsAsErrors</c> on code Caching.NET has no claim over.
    /// </summary>
    [Fact]
    public async Task RedisClientInConsumerCode_IsNotReported()
    {
        const string source = """
            using StackExchange.Redis;

            public class Consumer
            {
                public void Use(ConfigurationOptions options) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 0);
    }

    /// <summary>
    /// A constructed generic is a <c>GenericNameSyntax</c>, which is neither <c>IdentifierName</c> nor
    /// <c>QualifiedName</c> — so before <c>SyntaxKind.GenericName</c> was registered, a consumer could
    /// declare a variable of an engine generic and the analyzer stayed silent.
    /// <c>MaybeValue&lt;T&gt;</c> is a public generic of <c>ZiggyCreatures.Caching.Fusion</c>.
    /// </summary>
    [Fact]
    public async Task EngineGenericDeclarationInConsumerCode_IsReported()
    {
        const string source = """
            using ZiggyCreatures.Caching.Fusion;

            public class Consumer
            {
                public bool Use()
                {
                    MaybeValue<int> value = default;
                    return value.HasValue;
                }
            }
            """;

        // The declaration (GenericName) and the member access on it are two distinct references.
        await VerifyAsync(source, expectedDiagnostics: 2);
    }

    /// <summary>
    /// The precise thing the rule exists to prevent: an engine generic in the consumer's own public
    /// signature. Unreported before <c>SyntaxKind.GenericName</c> was registered.
    /// </summary>
    [Fact]
    public async Task EngineGenericReturnTypeInConsumerCode_IsReported()
    {
        const string source = """
            using ZiggyCreatures.Caching.Fusion;

            public class Consumer
            {
                public static MaybeValue<string> GenericReturn() => default;
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 1);
    }

    /// <summary>
    /// A fully-qualified constructed generic must still report exactly once: the enclosing
    /// <c>QualifiedNameSyntax</c> wins over its own trailing <c>GenericNameSyntax</c> child, the same
    /// way it wins over a trailing <c>IdentifierNameSyntax</c>.
    /// </summary>
    [Fact]
    public async Task FullyQualifiedEngineGeneric_ReportsOnce()
    {
        const string source = """
            public class Consumer
            {
                public static ZiggyCreatures.Caching.Fusion.MaybeValue<string> GenericReturn() => default;
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 1);
    }

    [Fact]
    public async Task CachingNetTypes_AreNotReported()
    {
        const string source = """
            using Caching.NET;
            using Caching.NET.Options;

            public class Consumer
            {
                public void Use(ICacheService cache)
                    => cache.Set("k", 1, new CacheEntryOverrides { Size = 1 });
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 0);
    }

    /// <summary>
    /// The <c>Caching.NET</c> assembly itself legitimately names both banned namespaces throughout
    /// <c>CacheFactoryContext</c>, <c>CachingBuilder</c> and <c>Options/RedisOptions</c>, not just in
    /// <c>Internal/</c>, so the exemption gates on the whole assembly rather than a folder.
    /// </summary>
    [Fact]
    public async Task CachingNetAssembly_IsExempt()
    {
        const string source = """
            using ZiggyCreatures.Caching.Fusion;

            public class Consumer
            {
                public void Use(IFusionCache cache) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 0, assemblyName: "Caching.NET");
    }

    /// <summary>
    /// Every <c>Caching.NET.Tests</c>* assembly (the unit, integration, chaos, properties and pod
    /// projects) legitimately names the engine to build and verify the adapter that hides it.
    /// </summary>
    [Fact]
    public async Task CachingNetTestsSubAssembly_IsExempt()
    {
        const string source = """
            using ZiggyCreatures.Caching.Fusion;

            public class Consumer
            {
                public void Use(IFusionCache cache) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 0, assemblyName: "Caching.NET.Tests.Sample");
    }

    /// <summary>
    /// The exemption must not accidentally widen to an assembly that merely starts with the same
    /// characters without being a genuine sub-namespace — <c>Caching.NET.TestsForMyApp</c> is a
    /// plausible consumer assembly name, not a Caching.NET test project.
    /// </summary>
    [Fact]
    public async Task AssemblyNameThatMerelyStartsWithTheExemptPrefix_IsNotExempt()
    {
        const string source = """
            using ZiggyCreatures.Caching.Fusion;

            public class Consumer
            {
                public void Use(IFusionCache cache) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 1, assemblyName: "Caching.NET.TestsForMyApp");
    }

    /// <summary>
    /// A single fully-qualified reference must report exactly once: the enclosing
    /// <c>QualifiedNameSyntax</c> wins over its own trailing <c>IdentifierNameSyntax</c> child.
    /// </summary>
    [Fact]
    public async Task FullyQualifiedEngineType_ReportsOnce()
    {
        const string source = """
            public class Consumer
            {
                public void Use(ZiggyCreatures.Caching.Fusion.IFusionCache cache) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 1);
    }

    /// <summary>
    /// Two distinct fully-qualified references on one line must not collapse into one diagnostic —
    /// the dedupe guard only suppresses a qualified name's own trailing name, not a second,
    /// unrelated reference.
    /// </summary>
    [Fact]
    public async Task TwoDistinctFullyQualifiedEngineTypesOnOneLine_ReportsTwice()
    {
        const string source = """
            public class Consumer
            {
                public void Use(ZiggyCreatures.Caching.Fusion.IFusionCache cache, ZiggyCreatures.Caching.Fusion.FusionCacheEntryOptions options) { }
            }
            """;

        await VerifyAsync(source, expectedDiagnostics: 2);
    }

    private static async Task VerifyAsync(string source, int expectedDiagnostics, string assemblyName = "AnalyzerSample")
    {
        var diagnostics = await AnalyzeAsync(source, assemblyName);

        Assert.Equal(expectedDiagnostics, diagnostics.Length);

        foreach (var diagnostic in diagnostics)
        {
            Assert.Equal(EngineTypeAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        }
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, string assemblyName = "AnalyzerSample")
    {
        // Force every assembly the sample sources can reference to load *before* enumerating
        // AppDomain.CurrentDomain.GetAssemblies(). Without this, whether a given engine assembly is
        // already loaded depends on test execution order: a `beforefieldinit` static field only
        // guarantees its type initializer runs eventually, not before this method's body, so the CLR
        // is free to defer loading until something else touches the assembly. That produced an
        // intermittent CS0246 (about 1 run in 3) when this test class happened to run before any
        // other code touched a given engine assembly. Touching the types here, inside the method that
        // actually needs them, makes the load unconditional.
        _ = typeof(IFusionCache).Assembly;
        _ = typeof(ConfigurationOptions).Assembly;
        _ = typeof(ICacheService).Assembly;
        _ = typeof(EngineTypeAnalyzer).Assembly;
        _ = typeof(object).Assembly;

        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // A compile error here would make an "no diagnostics" assertion meaningless.
        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(compileErrors.Length == 0, string.Join("\n", compileErrors.Select(d => d.ToString())));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new EngineTypeAnalyzer()));

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>
    /// The shipped analyzer must not reference a Roslyn newer than the compiler in the oldest SDK a
    /// consumer may build with.
    /// </summary>
    /// <remarks>
    /// This is a packaging guard, not a behaviour test. The analyzer ships inside the Caching.NET
    /// package, so the consumer's <c>csc</c> loads it — and refuses to, with
    /// <c>error CS9057: Analyzer assembly ... references version 'X' of the compiler, which is newer
    /// than the currently running version 'Y'</c>, when it was built against a newer Roslyn. That is
    /// a hard build failure in every consuming application, so the package becomes uninstallable
    /// while every in-repo build stays green: nothing here loads the analyzer through the compiler.
    /// Building the analyzer against Microsoft.CodeAnalysis.CSharp 5.6.0 shipped exactly that.
    /// <para>
    /// Raising <see cref="MaximumSupportedRoslynVersion"/> is a deliberate decision to raise the
    /// minimum SDK a consumer needs, and belongs in the release notes.
    /// </para>
    /// </remarks>
    [Fact]
    public void ShippedAnalyzer_TargetsARoslynOldEnoughForConsumerCompilers()
    {
        // The .NET 8, 9 and 10 SDK compilers all load an analyzer built against this or older.
        var maximum = new Version(MaximumSupportedRoslynVersion);

        var roslynReferences = typeof(EngineTypeAnalyzer).Assembly
            .GetReferencedAssemblies()
            .Where(a => a.Name is not null
                && a.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(roslynReferences);
        Assert.All(roslynReferences, reference => Assert.True(
            reference.Version <= maximum,
            $"the shipped analyzer references {reference.Name} {reference.Version}, newer than the "
            + $"supported ceiling {maximum}. Every consuming build would fail with CS9057. Lower "
            + "Microsoft.CodeAnalysis.CSharp in Directory.Packages.props."));
    }

    private const string MaximumSupportedRoslynVersion = "4.14.0.0";
}
