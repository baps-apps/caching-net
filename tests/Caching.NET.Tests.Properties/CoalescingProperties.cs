using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Caching.NET.Tests.Properties;

public class CoalescingProperties
{
    [Property(MaxTest = 20)]
    public bool N_concurrent_GetOrCreate_invokes_factory_at_most_once(PositiveInt n)
    {
        var concurrency = Math.Min(n.Get, 30);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddCaching(b => b.UseInMemory().WithKeyPrefix("coalesce-prop"));
        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<ICacheService>();

        int invocations = 0;
        var key = Guid.NewGuid().ToString("n");

        var tasks = new Task<string>[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = cache.GetOrCreateAsync(key, async _ =>
            {
                Interlocked.Increment(ref invocations);
                await Task.Delay(20);
                return "value";
            });
        }

        Task.WhenAll(tasks).GetAwaiter().GetResult();
        return invocations == 1;
    }
}
