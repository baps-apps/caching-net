using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Caching.NET.Analyzers;

/// <summary>
/// Reports per-call cache entry options constructed from scratch instead of derived from the
/// cache's configured defaults.
/// </summary>
/// <remarks>
/// <para>
/// Caching.NET expresses its cache mode through the cache's default entry options: Redis mode marks
/// entries to skip the memory layer, In-Memory mode marks them to skip the distributed layer, and
/// the configured durations, fail-safe settings and timeouts live there too. Per-call options
/// replace those defaults wholesale, and the engine offers no cache-wide switch that could reimpose
/// them.
/// </para>
/// <para>
/// The practical consequence: <c>await cache.SetAsync(key, value, new FusionCacheEntryOptions { ...
/// })</c> in Redis mode silently re-enables the local memory layer for that call, so the instance
/// can serve a value Redis has not confirmed. <c>cache.CreateEntryOptions(o =&gt; ...)</c> starts
/// from the configured defaults and keeps every one of those settings.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CacheEntryOptionsAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic id for constructing per-call entry options directly.</summary>
    public const string DiagnosticId = "CACHENET001";

    private const string OptionsTypeName = "ZiggyCreatures.Caching.Fusion.FusionCacheEntryOptions";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        title: "Derive per-call cache entry options from the cache",
        messageFormat:
            "Build entry options with cache.CreateEntryOptions(...) instead of constructing FusionCacheEntryOptions. "
            + "A constructed instance replaces the cache's configured defaults, which drops the mode's layer settings "
            + "(in Redis mode this re-enables the local memory layer for the call) along with the configured duration, "
            + "fail-safe and timeout values.",
        category: "Caching.NET",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Caching.NET applies its cache mode through the cache's default entry options. Per-call options replace "
            + "them entirely, so options that were not derived from the cache lose the mode's guarantees. "
            + "cache.CreateEntryOptions(o => ...) duplicates the configured defaults and then applies your changes.",
        helpLinkUri: "https://github.com/baps-apps/caching-net/blob/main/docs/ARCHITECTURE.md#3-mode-mapping");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var optionsType = compilationContext.Compilation.GetTypeByMetadataName(OptionsTypeName);
            if (optionsType is null)
            {
                // The application does not use the cache at all; nothing to say.
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(
                nodeContext => Analyze(nodeContext, optionsType),
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.ImplicitObjectCreationExpression);
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol optionsType)
    {
        var created = context.SemanticModel.GetTypeInfo(context.Node, context.CancellationToken).Type;
        if (created is null || !SymbolEqualityComparer.Default.Equals(created, optionsType))
        {
            return;
        }

        var location = context.Node is BaseObjectCreationExpressionSyntax creation
            ? creation.GetLocation()
            : context.Node.GetLocation();

        context.ReportDiagnostic(Diagnostic.Create(s_rule, location));
    }
}
