using Caching.NET;
using Caching.NET.Extensions;
using Caching.NET.Keys;
using Caching.NET.Options;
using Caching.NET.Sample.Data;
using Microsoft.AspNetCore.Mvc;

namespace Caching.NET.Sample.Controllers;

/// <summary>
/// Shows how an application consumes Caching.NET. The cache is injected as
/// <see cref="ICacheService"/> — Caching.NET's own operation contract — and named caches come from
/// <see cref="ICacheProvider"/>. Every configuration concern lives in <c>Program</c> and
/// <c>appsettings.json</c>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ProductCatalogController : ControllerBase
{
    private readonly ICacheService _cache;
    private readonly ICacheService _shortLived;
    private readonly ICacheGuard _guard;
    private readonly ProductRepository _repository;

    /// <summary>Creates the controller.</summary>
    /// <param name="cache">The default cache.</param>
    /// <param name="caches">Resolves named caches.</param>
    /// <param name="guard">Key and tag limits for the default cache.</param>
    /// <param name="repository">The sample data source.</param>
    public ProductCatalogController(
        ICacheService cache,
        ICacheProvider caches,
        ICacheGuard guard,
        ProductRepository repository)
    {
        _cache = cache;
        _shortLived = caches.GetCache("short-lived");
        _guard = guard;
        _repository = repository;
    }

    /// <summary>Reads one product, loading it on a miss. The common get-or-set pattern.</summary>
    /// <param name="sku">Product identifier.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("{sku}")]
    public async Task<ActionResult<Product>> GetAsync(string sku, CancellationToken cancellationToken)
    {
        var key = CacheKey.For<Product>(sku).Build();
        _guard.ValidateKey(key);

        var product = await _cache.GetOrSetAsync<Product?>(
            key,
            async (_, token) => await _repository.LoadAsync(sku, token),
            options: new CacheEntryOverrides
            {
                LocalExpiration = TimeSpan.FromMinutes(10),
                DistributedExpiration = TimeSpan.FromMinutes(10)
            },
            tags: [$"product:{sku}"],
            token: cancellationToken);

        return product is null ? NotFound() : Ok(product);
    }

    /// <summary>Reads a category, tagging every entry so the whole group can be invalidated at once.</summary>
    /// <param name="categoryId">Category identifier.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("category/{categoryId:int}")]
    public async Task<ActionResult<IReadOnlyList<Product>>> GetByCategoryAsync(
        int categoryId,
        CancellationToken cancellationToken)
    {
        var tags = new[] { $"category:{categoryId}" };
        _guard.ValidateTags(tags);

        var products = await _cache.GetOrSetAsync<IReadOnlyList<Product>>(
            CacheKey.For<Product>(categoryId).WithVariant("by-category").Build(),
            async (_, token) => await _repository.LoadByCategoryAsync(categoryId, token),
            options: new CacheEntryOverrides
            {
                LocalExpiration = TimeSpan.FromMinutes(5),
                DistributedExpiration = TimeSpan.FromMinutes(5),
                EagerRefreshThreshold = 0.8f
            },
            tags: tags,
            token: cancellationToken);

        return Ok(products);
    }

    /// <summary>Reads several products in one round of concurrent lookups.</summary>
    /// <param name="skus">Comma-separated product identifiers.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("batch")]
    public async Task<ActionResult<IReadOnlyDictionary<string, Product?>>> GetManyAsync(
        [FromQuery] string skus,
        CancellationToken cancellationToken)
    {
        var keys = skus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(sku => CacheKey.For<Product>(sku).Build());

        var cached = await _cache.GetManyAsync<Product>(keys, token: cancellationToken);
        return Ok(cached);
    }

    /// <summary>Invalidates one product across every instance.</summary>
    /// <param name="sku">Product identifier.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpDelete("{sku}")]
    public async Task<IActionResult> InvalidateAsync(string sku, CancellationToken cancellationToken)
    {
        await _cache.RemoveAsync(CacheKey.For<Product>(sku).Build(), token: cancellationToken);
        return NoContent();
    }

    /// <summary>Invalidates a whole category by tag.</summary>
    /// <param name="categoryId">Category identifier.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpDelete("category/{categoryId:int}")]
    public async Task<IActionResult> InvalidateCategoryAsync(int categoryId, CancellationToken cancellationToken)
    {
        await _cache.RemoveByTagAsync($"category:{categoryId}", token: cancellationToken);
        return NoContent();
    }

    /// <summary>Rate-limit style counter stored in the short-lived named cache.</summary>
    /// <param name="clientId">Caller identifier.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    [HttpGet("quota/{clientId}")]
    public async Task<ActionResult<int>> GetQuotaAsync(string clientId, CancellationToken cancellationToken)
    {
        var remaining = await _shortLived.GetOrSetAsync<int>(
            $"quota:{clientId}",
            async (_, _) => 100,
            token: cancellationToken);

        return Ok(remaining);
    }

    /// <summary>Reports how often the sample data source was actually read — cache effectiveness.</summary>
    [HttpGet("stats")]
    public ActionResult<object> GetStats() => Ok(new { SourceLoads = _repository.LoadCount });
}
