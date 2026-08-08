using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Caching.NET.Analyzers;

/// <summary>
/// Reports any direct reference to a Caching.NET internal cache engine type — a
/// <c>ZiggyCreatures.Caching.Fusion</c> symbol — in consumer code.
/// </summary>
/// <remarks>
/// <para>
/// Caching.NET owns its whole public surface — <c>ICacheService</c>, <c>CacheEntryOverrides</c>,
/// <c>CachingBuilder</c>, <c>CachingOptions</c> and friends — precisely so the internal cache engine
/// can be replaced without a source change in consuming applications. A direct reference to the
/// engine forfeits that guarantee: the consumer's build now depends on a package Caching.NET does not
/// promise to keep, to keep at the same version, or to keep exposing in a compatible shape.
/// </para>
/// <para>
/// <c>StackExchange.Redis</c> is deliberately <b>not</b> banned. It reaches a consumer as a
/// transitive dependency of Caching.NET, but an application may legitimately own a direct use of it —
/// a rate limiter, a distributed lock, pub/sub — that has nothing to do with the cache. Flagging that
/// would break the consumer's build under <c>TreatWarningsAsErrors</c> on code Caching.NET has no
/// claim over. The engine is what must stay hidden; the Redis client is not the engine.
/// </para>
/// <para>
/// The analyzer does not run against the <c>Caching.NET</c> assembly itself or against any
/// <c>Caching.NET.Tests</c>* assembly: both legitimately name the engine to build and verify the
/// adapter that hides it from everyone else.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EngineTypeAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic id for a direct reference to a Caching.NET internal cache engine type.</summary>
    public const string DiagnosticId = "CACHENET001";

    private const string CachingNetAssemblyName = "Caching.NET";
    private const string CachingNetTestsAssemblyPrefix = "Caching.NET.Tests";

    private static readonly string[] s_bannedNamespacePrefixes =
    [
        "ZiggyCreatures.Caching.Fusion"
    ];

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        title: "Caching.NET engine type referenced directly",
        messageFormat:
            "'{0}' is an implementation detail of Caching.NET, not part of its API. "
            + "Use Caching.NET's own types — ICacheService, CacheEntryOverrides, CachingBuilder, CachingOptions — "
            + "so this code keeps compiling if the internal cache engine changes.",
        category: "Caching.NET",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Caching.NET owns its whole public surface so the internal cache engine can be replaced without a "
            + "source change in consuming applications. Referencing an engine type directly forfeits that "
            + "guarantee.",
        helpLinkUri: "https://github.com/baps-apps/caching-net/blob/main/docs/ARCHITECTURE.md");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(s_rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(static compilationContext =>
        {
            if (IsExemptAssembly(compilationContext.Compilation.AssemblyName))
            {
                // Caching.NET itself, and its test assemblies, legitimately name the engine to build
                // and verify the adapter that hides it from everyone else.
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(
                Analyze,
                SyntaxKind.IdentifierName,
                SyntaxKind.QualifiedName,
                // A constructed generic is a GenericNameSyntax, which is neither of the above. Without
                // this kind, `MaybeValue<T>` and `FusionCacheFactoryExecutionContext<T>` — public
                // generics of the engine, and the engine types a consumer is most likely to touch —
                // went unreported even in a consumer's own public signature, which is the precise
                // thing this rule exists to prevent.
                SyntaxKind.GenericName);
        });
    }

    private static bool IsExemptAssembly(string? assemblyName)
        => assemblyName is CachingNetAssemblyName or CachingNetTestsAssemblyPrefix
            || (assemblyName is not null
                && assemblyName.StartsWith(CachingNetTestsAssemblyPrefix + ".", StringComparison.Ordinal));

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        // A qualified name's own trailing name (e.g. the "Fusion" in "...Caching.Fusion", or the
        // "MaybeValue<int>" in "...Fusion.MaybeValue<int>") is a distinct node in the tree —
        // IdentifierName for a plain type, GenericName for a constructed one. Reporting it as well as
        // the enclosing QualifiedName would double-report the same reference; let the enclosing node
        // win. Matching SimpleNameSyntax covers both, which matters now that GenericName is a
        // registered kind.
        if (context.Node is SimpleNameSyntax { Parent: QualifiedNameSyntax qualified } name
            && qualified.Right == name)
        {
            return;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
        if (symbol is null or INamespaceSymbol)
        {
            // A namespace segment — including every part of a using directive — names where a type
            // lives, not the type itself; only the type or member reference is worth reporting.
            return;
        }

        var containingNamespace = symbol.ContainingNamespace?.ToDisplayString();
        if (containingNamespace is null || !IsBannedNamespace(containingNamespace))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(s_rule, context.Node.GetLocation(), symbol.ToDisplayString()));
    }

    private static bool IsBannedNamespace(string containingNamespace)
    {
        foreach (var prefix in s_bannedNamespacePrefixes)
        {
            if (containingNamespace.Equals(prefix, StringComparison.Ordinal)
                || containingNamespace.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
