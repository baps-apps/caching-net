using System;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Caching.NET.Analyzers;

/// <summary>
/// CN0001: flags high-cardinality tag keys on OTel instrument calls and high-cardinality placeholders
/// in logging message templates / scopes. Forbidden: key, tenant, user_id (and cache.* variants).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CardinalityAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic identifier for this rule.</summary>
    public const string DiagnosticId = "CN0001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "High-cardinality tag or log property in Caching.NET telemetry path",
        messageFormat: "High-cardinality name '{0}' is forbidden on Caching.NET OTel instruments and structured log templates",
        category: "Caching.Cardinality",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "High-cardinality names cause time-series explosion; use cache.payload.bytes for payload size instead of tagging raw keys.",
        helpLinkUri: "https://github.com/baps-apps/caching-net/blob/main/docs/superpowers/specs/2026-05-05-v2-amazon-scale-design.md#7-observability-otel-first");

    private static readonly ImmutableHashSet<string> ForbiddenKeys =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "key", "tenant", "user_id", "cache.key", "cache.tenant", "cache.user_id");

    private static readonly ImmutableHashSet<string> InstrumentMethodNames =
        ImmutableHashSet.Create("Add", "Record");

    private static readonly ImmutableHashSet<string> LoggerTemplateMethodNames =
        ImmutableHashSet.Create(
            "Log", "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical",
            "BeginScope");

    private static readonly Regex s_templatePlaceholder =
        new(@"\{([^{}:@]+)(?::[^}]*)?\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        if (InstrumentMethodNames.Contains(member.Name.Identifier.Text))
            AnalyzeInstrumentInvocation(context, invocation, member);

        if (LoggerTemplateMethodNames.Contains(member.Name.Identifier.Text))
            AnalyzeLoggerInvocation(context, invocation, member);
    }

    private static void AnalyzeInstrumentInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax member)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null) return;

        var containingTypeName = symbol.ContainingType?.OriginalDefinition?.ToDisplayString();
        if (containingTypeName is null) return;
        if (!IsInstrumentType(containingTypeName)) return;

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (TryGetForbiddenConstantTagKey(arg.Expression, context.SemanticModel, context.CancellationToken, out var bad))
                context.ReportDiagnostic(Diagnostic.Create(Rule, arg.GetLocation(), bad));
        }
    }

    private static void AnalyzeLoggerInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax member)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (symbol is null) return;

        if (!IsLoggerTemplateMethod(symbol, member.Name.Identifier.Text)) return;

        var templateArg = FindMessageTemplateArgument(invocation, context.SemanticModel, context.CancellationToken);
        if (templateArg is null) return;

        var cv = context.SemanticModel.GetConstantValue(templateArg.Expression, context.CancellationToken);
        if (!cv.HasValue || cv.Value is not string template) return;

        foreach (Match m in s_templatePlaceholder.Matches(template))
        {
            var name = m.Groups[1].Value;
            if (name.Length > 0 && ForbiddenKeys.Contains(name))
                context.ReportDiagnostic(Diagnostic.Create(Rule, templateArg.GetLocation(), name));
        }
    }

    /// <summary>
    /// First argument whose static type is string (message template / scope format candidate).
    /// The caller later requires compile-time constant string content.
    /// </summary>
    private static ArgumentSyntax? FindMessageTemplateArgument(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken ct)
    {
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            // Fast path for common direct string literals.
            if (arg.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            {
                return arg;
            }

            var typeInfo = semanticModel.GetTypeInfo(arg.Expression, ct);
            var argType = typeInfo.ConvertedType ?? typeInfo.Type;
            if (argType?.SpecialType == SpecialType.System_String)
            {
                return arg;
            }
        }

        return null;
    }

    private static bool IsLoggerTemplateMethod(IMethodSymbol symbol, string name)
    {
        if (!LoggerTemplateMethodNames.Contains(name)) return false;

        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        if (!ns.Contains("Microsoft.Extensions.Logging", StringComparison.Ordinal)) return false;

        if (name == "BeginScope")
        {
            if (symbol.Parameters.Length == 0) return false;
            return symbol.Parameters[0].Type.SpecialType == SpecialType.System_String;
        }

        return symbol.Name == name;
    }

    private static bool IsInstrumentType(string typeName) =>
        typeName == "System.Diagnostics.Metrics.Counter<T>" ||
        typeName == "System.Diagnostics.Metrics.Histogram<T>" ||
        typeName == "System.Diagnostics.Metrics.UpDownCounter<T>";

    private static bool TryGetForbiddenConstantTagKey(
        ExpressionSyntax expr,
        SemanticModel model,
        CancellationToken ct,
        out string? forbidden)
    {
        forbidden = null;
        if (TryGetConstantStringKey(expr, model, ct, out var key) && key is not null && ForbiddenKeys.Contains(key))
        {
            forbidden = key;
            return true;
        }
        return false;
    }

    private static bool TryGetConstantStringKey(
        ExpressionSyntax expr,
        SemanticModel model,
        CancellationToken ct,
        out string? key)
    {
        key = null;

        // new KeyValuePair<string, ...>("k", v) or new("k", v)
        if (expr is ObjectCreationExpressionSyntax obj && obj.ArgumentList?.Arguments.Count >= 1)
        {
            return TryConstantString(obj.ArgumentList.Arguments[0].Expression, model, ct, out key);
        }

        if (expr is ImplicitObjectCreationExpressionSyntax imp && imp.ArgumentList?.Arguments.Count >= 1)
        {
            return TryConstantString(imp.ArgumentList.Arguments[0].Expression, model, ct, out key);
        }

        // KeyValuePair.Create("k", v)
        if (expr is InvocationExpressionSyntax inv
            && inv.Expression is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.Text == "Create"
            && inv.ArgumentList.Arguments.Count >= 1)
        {
            if (model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol cm
                && cm.ContainingType?.Name == "KeyValuePair"
                && TryConstantString(inv.ArgumentList.Arguments[0].Expression, model, ct, out key))
                return true;
        }

        return false;
    }

    private static bool TryConstantString(
        ExpressionSyntax expr,
        SemanticModel model,
        CancellationToken ct,
        out string? value)
    {
        value = null;
        var cv = model.GetConstantValue(expr, ct);
        if (cv.HasValue && cv.Value is string s)
        {
            value = s;
            return true;
        }
        return false;
    }
}
