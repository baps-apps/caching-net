using Caching.NET.Abstractions;
using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.AspNetCore.Mvc;

namespace Caching.NET.Sample.Controllers;

/// <summary>
/// Sample controller that demonstrates the key Caching.NET usage patterns:
/// global-mode caching, per-call mode overrides, bypass, force-refresh, and tag-based invalidation.
/// </summary>
[ApiController]
[Route("catalog")]
public class ProductCatalogController : ControllerBase
{
    // Basic, fake in-memory data source to simulate a database or external service.
    private static readonly Product[] AllProducts =
    [
        new("p-100", "Gaming Laptop", "electronics", 1799.00m),
        new("p-101", "Noise Cancelling Headphones", "electronics", 299.00m),
        new("p-200", "Ergonomic Office Chair", "furniture", 499.00m),
        new("p-300", "Stainless Steel Water Bottle", "home", 29.00m),
    ];

    /// <summary>
    /// Returns the full product catalog, cached using the globally configured mode (Hybrid by default).
    /// Demonstrates the basic <c>GetOrCreateAsync</c> pattern with explicit expiration values.
    /// </summary>
    /// <param name="cache">Cache service injected per-request via <c>[FromServices]</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("products")]
    public Task<IEnumerable<Product>> GetProducts(
        [FromServices] ICacheService cache,
        CancellationToken cancellationToken)
    {
        return cache.GetOrCreateAsync(
            key: "catalog:all",
            factory: _ => Task.FromResult<IEnumerable<Product>>(AllProducts),
            expiration: TimeSpan.FromMinutes(5),
            localExpiration: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Returns the featured product subset, always served from the in-process memory cache
    /// regardless of the application-level cache mode.
    /// Demonstrates <see cref="CacheCallOptions.Mode"/> to pin a hot path to <see cref="CacheMode.InMemory"/>.
    /// </summary>
    /// <param name="cache">Cache service injected per-request via <c>[FromServices]</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("featured")]
    public Task<IEnumerable<Product>> GetFeaturedInMemory(
        [FromServices] ICacheService cache,
        CancellationToken cancellationToken)
    {
        var callOptions = new CacheCallOptions
        {
            Mode = CacheMode.InMemory
        };

        return cache.GetOrCreateAsync(
            key: "catalog:featured",
            factory: _ => Task.FromResult(AllProducts.Take(2)),
            callOptions: callOptions,
            expiration: TimeSpan.FromMinutes(2),
            localExpiration: null,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Returns the full product catalog fetched directly from the source, bypassing all cache tiers.
    /// Demonstrates <see cref="CacheCallOptions.BypassCache"/> for diagnostics or emergency "cache off" scenarios.
    /// </summary>
    /// <param name="cache">Cache service injected per-request via <c>[FromServices]</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("products/raw")]
    public Task<IEnumerable<Product>> GetProductsBypassingCache(
        [FromServices] ICacheService cache,
        CancellationToken cancellationToken)
    {
        var callOptions = new CacheCallOptions
        {
            BypassCache = true
        };

        return cache.GetOrCreateAsync(
            key: "catalog:raw",
            factory: _ => Task.FromResult<IEnumerable<Product>>(AllProducts),
            callOptions: callOptions,
            expiration: TimeSpan.FromMinutes(1),
            localExpiration: null,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Returns the specified product, always recomputing the value from the source and overwriting the cache entry.
    /// Demonstrates <see cref="CacheCallOptions.ForceRefresh"/> to proactively refresh stale cached data
    /// without first removing the key.
    /// </summary>
    /// <param name="cache">Cache service injected per-request via <c>[FromServices]</c>.</param>
    /// <param name="id">The product identifier.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The product, or <c>404 Not Found</c> when the identifier does not exist.</returns>
    [HttpGet("products/{id}/force-refresh")]
    public async Task<ActionResult<Product>> GetProductWithForceRefresh(
        [FromServices] ICacheService cache,
        string id,
        CancellationToken cancellationToken)
    {
        var callOptions = new CacheCallOptions
        {
            ForceRefresh = true
        };

        var product = await cache.GetOrCreateAsync(
            key: $"product:{id}",
            factory: _ =>
            {
                var found = AllProducts.FirstOrDefault(p => p.Id == id);
                if (found is null)
                {
                    throw new KeyNotFoundException($"Product '{id}' was not found.");
                }

                return Task.FromResult(found);
            },
            callOptions: callOptions,
            expiration: TimeSpan.FromMinutes(10),
            localExpiration: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

        return Ok(product);
    }

    /// <summary>
    /// Evicts all cache entries associated with the specified category tag.
    /// In Hybrid mode this leverages <c>HybridCache</c> tag support.
    /// In InMemory or Redis modes the call is a safe no-op (logs a debug message and returns no content).
    /// Demonstrates <see cref="ICacheService.RemoveByTagAsync(string, CancellationToken)"/> for tag-based invalidation.
    /// </summary>
    /// <param name="cache">Cache service injected per-request via <c>[FromServices]</c>.</param>
    /// <param name="category">The category tag whose associated cache entries should be evicted.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns><c>204 No Content</c> on success, or <c>400 Bad Request</c> when <paramref name="category"/> is blank.</returns>
    [HttpDelete("categories/{category}/invalidate")]
    public async Task<IActionResult> InvalidateCategory(
        [FromServices] ICacheService cache,
        string category,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return BadRequest("Category is required.");
        }

        await cache.RemoveByTagAsync(category, cancellationToken);
        return NoContent();
    }

    /// <summary>Represents a product in the sample catalog.</summary>
    /// <param name="Id">Unique product identifier (e.g. <c>"p-100"</c>).</param>
    /// <param name="Name">Display name of the product.</param>
    /// <param name="Category">Category tag used for Hybrid-mode cache invalidation.</param>
    /// <param name="Price">Retail price in the default currency.</param>
    public record Product(string Id, string Name, string Category, decimal Price);
}

