using System.Collections.Concurrent;
using System.Reflection;
using Caching.NET.Abstractions;

namespace Caching.NET.Internal;

/// <summary>
/// Bridges the runtime-typed <c>GetAsync(string, Type, CancellationToken)</c> overload onto the
/// generic <see cref="ICacheService.GetAsync{T}"/> via a cached reflection invoker. Used both by the
/// default interface method on <see cref="ICacheService"/> (so external implementations keep working)
/// and by built-in services whose backing store only exposes a generic API (Hybrid). The per-type
/// invoker is cached so repeated reads avoid re-resolving the closed generic method.
/// </summary>
internal static class RuntimeTypedCacheInvoker
{
    private static readonly MethodInfo GenericGetAsyncDefinition = typeof(ICacheService)
        .GetMethods()
        .Single(m => m is { Name: nameof(ICacheService.GetAsync), IsGenericMethodDefinition: true }
            && m.GetParameters().Length == 2);

    private static readonly ConcurrentDictionary<Type, Func<ICacheService, string, CancellationToken, Task<object?>>> Invokers = new();

    public static Task<object?> GetAsync(ICacheService service, string key, Type type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(type);
        var invoker = Invokers.GetOrAdd(type, BuildInvoker);
        return invoker(service, key, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "Reflection fallback for external ICacheService implementations and the Hybrid backend, whose store only exposes a generic API; the JSON/MessagePack reflection paths are already trim-suppressed at the project level.")]
    private static Func<ICacheService, string, CancellationToken, Task<object?>> BuildInvoker(Type type)
    {
        var closed = GenericGetAsyncDefinition.MakeGenericMethod(type);
        var resultProperty = typeof(Task<>).MakeGenericType(type).GetProperty(nameof(Task<object>.Result))!;
        return async (service, key, cancellationToken) =>
        {
            var task = (Task)closed.Invoke(service, [key, cancellationToken])!;
            await task.ConfigureAwait(false);
            return resultProperty.GetValue(task);
        };
    }
}
