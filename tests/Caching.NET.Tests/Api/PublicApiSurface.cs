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
        // Property and event accessors are covered by the property/event entry itself.
        MethodInfo method => !method.IsSpecialName,
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

    private static string Describe(MemberInfo member) => member switch
    {
        Type type => Describe(type),
        MethodInfo method =>
            $"{Describe(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{Describe(p.ParameterType)} {p.Name}"))})",
        ConstructorInfo constructor =>
            $".ctor({string.Join(", ", constructor.GetParameters().Select(p => $"{Describe(p.ParameterType)} {p.Name}"))})",
        PropertyInfo property =>
            $"{Describe(property.PropertyType)} {property.Name} {{ {(property.GetGetMethod() is not null ? "get; " : string.Empty)}{(property.GetSetMethod() is not null ? "set; " : string.Empty)}}}",
        FieldInfo field => $"{Describe(field.FieldType)} {field.Name}",
        EventInfo @event => $"event {Describe(@event.EventHandlerType!)} {@event.Name}",
        _ => $"{member.MemberType} {member.Name}"
    };

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

        var name = type.FullName ?? type.Name;
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
