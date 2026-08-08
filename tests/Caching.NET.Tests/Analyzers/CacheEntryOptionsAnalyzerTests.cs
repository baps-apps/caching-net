using System.Collections.Immutable;
using System.Reflection;
using Caching.NET.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Tests.Analyzers;

/// <summary>
/// The shipped analyzer, run against real compilations.
/// </summary>
/// <remarks>
/// The diagnostic is the mitigation for the one guarantee Caching.NET cannot enforce at runtime:
/// per-call entry options replace the cache's defaults, so options built with <c>new</c> silently
/// drop the mode's layer settings. If the analyzer stops firing, that gap becomes silent again.
/// </remarks>
public class CacheEntryOptionsAnalyzerTests
{
    [Fact]
    public async Task ConstructedEntryOptions_AreReported()
    {
        var diagnostics = await AnalyzeAsync("""
            using ZiggyCreatures.Caching.Fusion;

            internal static class Sample
            {
                public static void Use(IFusionCache cache)
                {
                    var options = new FusionCacheEntryOptions();
                    cache.Set("k", 1, options);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(CacheEntryOptionsAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("CreateEntryOptions", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConstructedEntryOptions_AreReportedWhenPassedInline()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using ZiggyCreatures.Caching.Fusion;

            internal static class Sample
            {
                public static void Use(IFusionCache cache)
                    => cache.Set("k", 1, new FusionCacheEntryOptions { Duration = TimeSpan.FromMinutes(1) });
            }
            """);

        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task ImplicitlyConstructedEntryOptions_AreReported()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using ZiggyCreatures.Caching.Fusion;

            internal static class Sample
            {
                public static void Use(IFusionCache cache)
                {
                    FusionCacheEntryOptions options = new() { Duration = TimeSpan.FromMinutes(1) };
                    cache.Set("k", 1, options);
                }
            }
            """);

        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task OptionsDerivedFromTheCache_AreNotReported()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using ZiggyCreatures.Caching.Fusion;

            internal static class Sample
            {
                public static void Use(IFusionCache cache)
                {
                    var options = cache.CreateEntryOptions(o => o.Duration = TimeSpan.FromMinutes(1));
                    cache.Set("k", 1, options);
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CallsWithoutPerCallOptions_AreNotReported()
    {
        var diagnostics = await AnalyzeAsync("""
            using ZiggyCreatures.Caching.Fusion;

            internal static class Sample
            {
                public static void Use(IFusionCache cache) => cache.Set("k", 1);
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CodeThatDoesNotUseTheCache_IsNotAnalyzed()
    {
        var diagnostics = await AnalyzeAsync("""
            internal static class Sample
            {
                public static object Use() => new object();
            }
            """);

        Assert.Empty(diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var references = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "AnalyzerSample",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // A compile error here would make an "no diagnostics" assertion meaningless.
        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(compileErrors.Length == 0, string.Join("\n", compileErrors.Select(d => d.ToString())));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new CacheEntryOptionsAnalyzer()));

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    // Referenced so the compilation above can resolve the cache types from the loaded assemblies.
    private static readonly Assembly s_engine = typeof(IFusionCache).Assembly;
}
