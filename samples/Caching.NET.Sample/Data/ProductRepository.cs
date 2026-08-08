namespace Caching.NET.Sample.Data;

/// <summary>A cached product.</summary>
/// <param name="Sku">Stock-keeping unit.</param>
/// <param name="Name">Display name.</param>
/// <param name="CategoryId">Owning category.</param>
/// <param name="PriceCents">Price in cents.</param>
/// <param name="LoadedAt">When the value was produced, so cache hits are visible in responses.</param>
public sealed record Product(string Sku, string Name, int CategoryId, int PriceCents, DateTimeOffset LoadedAt);

/// <summary>
/// Stands in for a database or upstream service. The artificial delay makes cache hits obvious in
/// the sample's response times.
/// </summary>
public sealed class ProductRepository
{
    private static readonly string[] Names = ["Hammer", "Wrench", "Drill", "Saw", "Pliers"];

    private int _loadCount;

    /// <summary>Number of times the sample "database" has actually been read.</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>Loads one product.</summary>
    /// <param name="sku">Product identifier.</param>
    /// <param name="cancellationToken">Cancellation token, propagated from the request.</param>
    public async Task<Product?> LoadAsync(string sku, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        await Task.Delay(250, cancellationToken);

        if (!sku.StartsWith("SKU-", StringComparison.Ordinal))
        {
            return null;
        }

        var index = Math.Abs(sku.GetHashCode(StringComparison.Ordinal)) % Names.Length;
        return new Product(sku, Names[index], index % 2, 1999 + index, DateTimeOffset.UtcNow);
    }

    /// <summary>Loads every product in a category.</summary>
    /// <param name="categoryId">Category identifier.</param>
    /// <param name="cancellationToken">Cancellation token, propagated from the request.</param>
    public async Task<IReadOnlyList<Product>> LoadByCategoryAsync(int categoryId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        await Task.Delay(250, cancellationToken);

        return [.. Enumerable.Range(0, Names.Length)
            .Where(i => i % 2 == categoryId % 2)
            .Select(i => new Product($"SKU-{i:000}", Names[i], categoryId, 1999 + i, DateTimeOffset.UtcNow))];
    }
}
