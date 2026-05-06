using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Caching.NET.Analyzers;

/// <summary>
/// CN0001: flags high-cardinality tag-key string literals on OTel instrument calls.
/// Forbidden keys: "key", "tenant", "user_id". Severity: Error.
/// Triggers on calls to Counter&lt;T&gt;.Add, Histogram&lt;T&gt;.Record, UpDownCounter&lt;T&gt;.Add
/// when any KeyValuePair&lt;string,object?&gt; argument's Key string literal matches a forbidden value.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CardinalityAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic identifier for this rule.</summary>
    public const string DiagnosticId = "CN0001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "High-cardinality tag added to a Caching.NET OTel instrument",
        messageFormat: "Tag key '{0}' is high-cardinality and forbidden on Caching.NET instruments. Use a histogram or remove the tag.",
        category: "Caching.Cardinality",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Adding key/tenant/user_id as a tag to OTel counters or histograms causes time-series explosion. Use the cache.payload.bytes histogram for size, and never tag with the raw key.",
        helpLinkUri: "https://github.com/baps-apps/caching-net/blob/main/docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md#7-observability-otel-first");

    private static readonly ImmutableHashSet<string> ForbiddenKeys =
        ImmutableHashSet.Create("key", "tenant", "user_id", "cache.key", "cache.tenant", "cache.user_id");

    private static readonly ImmutableHashSet<string> InstrumentMethodNames =
        ImmutableHashSet.Create("Add", "Record");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;
        if (!InstrumentMethodNames.Contains(member.Name.Identifier.Text)) return;

        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null) return;

        var containingTypeName = symbol.ContainingType?.OriginalDefinition?.ToDisplayString();
        if (containingTypeName is null) return;
        if (!IsInstrumentType(containingTypeName)) return;

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var literalKey = ExtractTagKeyLiteral(arg.Expression);
            if (literalKey is null) continue;
            if (!ForbiddenKeys.Contains(literalKey)) continue;
            context.ReportDiagnostic(Diagnostic.Create(Rule, arg.GetLocation(), literalKey));
        }
    }

    private static bool IsInstrumentType(string typeName) =>
        typeName == "System.Diagnostics.Metrics.Counter<T>" ||
        typeName == "System.Diagnostics.Metrics.Histogram<T>" ||
        typeName == "System.Diagnostics.Metrics.UpDownCounter<T>" ||
        typeName == "System.Diagnostics.Metrics.ObservableCounter<T>" ||
        typeName == "System.Diagnostics.Metrics.ObservableUpDownCounter<T>";

    private static string? ExtractTagKeyLiteral(ExpressionSyntax expr)
    {
        if (expr is ObjectCreationExpressionSyntax obj && obj.ArgumentList?.Arguments.Count >= 1)
            return TryGetStringLiteral(obj.ArgumentList.Arguments[0].Expression);

        if (expr is ImplicitObjectCreationExpressionSyntax imp && imp.ArgumentList?.Arguments.Count >= 1)
            return TryGetStringLiteral(imp.ArgumentList.Arguments[0].Expression);

        return null;
    }

    private static string? TryGetStringLiteral(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText
            : null;
}
