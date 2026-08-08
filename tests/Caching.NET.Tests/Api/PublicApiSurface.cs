using System.Reflection;
using System.Text;

namespace Caching.NET.Tests.Api;

/// <summary>
/// Renders the assembly's public surface as stable, sorted text so it can be compared against a
/// committed baseline.
/// </summary>
/// <remarks>
/// Deliberately reflection-based rather than an ApiCompat package baseline: a package baseline needs
/// a previously *published* version to compare against, and it would only catch changes at pack
/// time. This runs on every test pass and needs nothing published.
/// </remarks>
internal static class PublicApiSurface
{
    public static string Render(Assembly assembly)
    {
        var builder = new StringBuilder();

        foreach (var type in assembly.GetExportedTypes().OrderBy(Describe, StringComparer.Ordinal))
        {
            builder.Append(Kind(type)).Append(' ').AppendLine(Describe(type));

            foreach (var member in Members(type))
            {
                builder.Append("    ").AppendLine(member);
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<string> Members(Type type)
        => type
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(Include)
            .Select(Describe)
            .OrderBy(m => m, StringComparer.Ordinal);

    private static bool Include(MemberInfo member) => member switch
    {
        // Property and event accessors are covered by the property/event entry itself, but operators
        // are real public API: removing CacheValue<T>.operator == is a breaking change, and excluding
        // every IsSpecialName method made it invisible to the baseline.
        MethodInfo method => !method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal),
        Type nested => false,
        _ => true
    };

    private static string Kind(Type type)
    {
        if (type.IsEnum)
        {
            return "enum";
        }

        if (type.IsInterface)
        {
            return "interface";
        }

        if (type.IsValueType)
        {
            return "struct";
        }

        return type.IsAbstract && type.IsSealed ? "static class" : "class";
    }

    // Nullability, parameter defaults and operators are all part of the contract a consumer compiles
    // against, so the baseline renders them: without them a string? -> string flip, a changed default,
    // or a deleted operator would all pass this gate green.
    private static readonly NullabilityInfoContext Nullability = new();

    private static string Describe(MemberInfo member) => member switch
    {
        Type type => Describe(type),
        MethodInfo method =>
            $"{DescribeReturn(method)} {method.Name}{DescribeGenericParameters(method)}({DescribeParameters(method.GetParameters())})",
        ConstructorInfo constructor =>
            $".ctor({DescribeParameters(constructor.GetParameters())})",
        PropertyInfo property =>
            $"{Annotate(property.PropertyType, Nullability.Create(property).ReadState)} {property.Name} {{ {(property.GetGetMethod() is not null ? "get; " : string.Empty)}{(property.GetSetMethod() is not null ? "set; " : string.Empty)}}}",
        FieldInfo field => $"{Annotate(field.FieldType, Nullability.Create(field).ReadState)} {field.Name}",
        EventInfo @event => $"event {Describe(@event.EventHandlerType!)} {@event.Name}",
        _ => $"{member.MemberType} {member.Name}"
    };

    private static string DescribeReturn(MethodInfo method)
        => Annotate(method.ReturnType, Nullability.Create(method.ReturnParameter).ReadState);

    /// <summary>
    /// Renders a generic method's own type parameters, which are otherwise invisible here.
    /// </summary>
    /// <remarks>
    /// A method's type parameters only appear in the rendered text when one of them happens to be used
    /// by a parameter or the return type. <c>CacheKey.For&lt;T&gt;(object)</c> uses <c>T</c> for neither,
    /// so it rendered as <c>For(System.Object id)</c> — meaning a change to <c>For&lt;T, TId&gt;</c>, or
    /// dropping the type parameter altogether, produced an identical baseline and passed this gate
    /// green. Both are source-breaking for every caller. <c>CacheExtensions.ExistsAsync&lt;TValue&gt;</c>
    /// had the same hole.
    /// </remarks>
    private static string DescribeGenericParameters(MethodInfo method)
        => method.IsGenericMethodDefinition
            ? $"<{string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name))}>"
            : string.Empty;

    private static string DescribeParameters(ParameterInfo[] parameters)
        => string.Join(", ", parameters.Select(Describe));

    private static string Describe(ParameterInfo parameter)
    {
        var rendered = $"{Annotate(parameter.ParameterType, Nullability.Create(parameter).WriteState)} {parameter.Name}";
        return parameter.HasDefaultValue ? $"{rendered} = {DescribeDefault(parameter)}" : rendered;
    }

    private static string DescribeDefault(ParameterInfo parameter) => parameter.DefaultValue switch
    {
        // Reflection reports default(T) as null for every T. Rendering "null" for a struct or an
        // unconstrained type parameter would be a lie -- default(CancellationToken) is not null.
        null => parameter.ParameterType.IsValueType || parameter.ParameterType.IsGenericParameter
            ? "default"
            : "null",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(parameter.DefaultValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };

    // Only reference types carry a nullable annotation worth rendering: a nullable value type is
    // already visible as System.Nullable<T> in the type name itself.
    //
    // Known limitation: this renders the OUTER type's nullability only. A declared
    // ValueTask<TValue?> is rendered "ValueTask<TValue>" (see PublicApi.approved.txt), so flipping
    // a generic argument from TValue? to TValue passes the gate. That is not binary-breaking and
    // barely source-breaking, and every type arity, method type-parameter list, name, parameter,
    // default-value and operator change is still caught — but do not read the baseline as proof of
    // inner nullability.
    private static string Annotate(Type type, NullabilityState state)
    {
        var rendered = Describe(type);
        return !type.IsValueType && state == NullabilityState.Nullable ? rendered + "?" : rendered;
    }

    private static string Describe(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return Describe(type.GetElementType()!) + "[]";
        }

        if (type.IsByRef)
        {
            return "ref " + Describe(type.GetElementType()!);
        }

        // FullName is null for a constructed generic containing a type parameter. Falling back to
        // Name alone stripped the namespace, which hid engine types from the baseline diff.
        var name = type.FullName ?? (type.Namespace is null ? type.Name : $"{type.Namespace}.{type.Name}");
        if (!type.IsGenericType)
        {
            return name;
        }

        var arity = name.IndexOf('`', StringComparison.Ordinal);
        if (arity >= 0)
        {
            name = name[..arity];
        }

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>";
    }
}
