using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
services.AddCaching(b =>
    b.UseInMemory()
     .WithKeyPrefix("aot-smoke")
     .WithSerializer(new JsonCacheSerializer(AppJsonContext.Default)));

var sp = services.BuildServiceProvider();
var cache = sp.GetRequiredService<ICacheService>();

var got = await cache.GetOrCreateAsync(
    "k",
    _ => Task.FromResult(new Order { Id = 42, Customer = "Acme" }));

Console.WriteLine($"Got Order Id={got.Id} Customer={got.Customer}");
return got.Id == 42 ? 0 : 1;

public sealed class Order
{
    public int Id { get; set; }
    public string? Customer { get; set; }
}
