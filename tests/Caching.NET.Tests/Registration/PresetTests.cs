using Caching.NET.Extensions;
using Caching.NET.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Caching.NET.Tests.Registration;

/// <summary>
/// <c>UseProductionDefaults()</c> and fail-safe's startup rule — <c>FailSafeMaxDuration</c> must be at
/// least <c>DefaultExpiration</c>. The preset used to set a flat 2 h, which silently shortened a longer
/// window configured before it, and failed startup outright after a <c>DefaultExpiration</c> over 2 h.
/// </summary>
public class PresetTests
{
    [Fact]
    public void ProductionDefaults_UseATwoHourFailSafeWindow()
    {
        var options = Resolve(cache => cache.UseProductionDefaults());

        Assert.True(options.Resilience.FailSafeEnabled);
        Assert.Equal(TimeSpan.FromHours(2), options.Resilience.FailSafeMaxDuration);
    }

    [Fact]
    public void ProductionDefaults_NeverShortenALongerFailSafeWindow()
    {
        var options = Resolve(cache => cache
            .WithFailSafe(maxDuration: TimeSpan.FromHours(6))
            .UseProductionDefaults());

        Assert.Equal(TimeSpan.FromHours(6), options.Resilience.FailSafeMaxDuration);
    }

    /// <summary>
    /// Raising the window only to <c>DefaultExpiration</c> would pass validation and leave no
    /// fallback at all — the physical lifetime is the longer of the two, not their sum — so the preset
    /// keeps its 2 h of fallback past the expiration.
    /// </summary>
    [Fact]
    public void ProductionDefaults_AfterALongExpiration_KeepATwoHourFallbackPastIt()
    {
        var options = Resolve(cache => cache
            .WithDefaultExpiration(TimeSpan.FromHours(3))
            .UseProductionDefaults());

        Assert.Equal(TimeSpan.FromHours(5), options.Resilience.FailSafeMaxDuration);
    }

    /// <summary>
    /// Builder calls made after a preset override it, so the preset cannot see a later expiration.
    /// Startup validation catches the combination and names the fix.
    /// </summary>
    [Fact]
    public void ALongExpirationAfterTheProductionDefaults_FailsStartupNamingTheFix()
    {
        var failure = Assert.Throws<OptionsValidationException>(() => Resolve(cache => cache
            .UseProductionDefaults()
            .WithDefaultExpiration(TimeSpan.FromHours(3))));

        Assert.Contains("WithFailSafe(maxDuration:", failure.Message, StringComparison.Ordinal);
    }

    private static CachingOptions Resolve(Action<CachingBuilder> configure)
    {
        using var provider = TestHost.Build(cache =>
        {
            cache.UseInMemory().WithApplicationPrefix("presets");
            configure(cache);
        });

        return provider.GetRequiredService<IOptionsMonitor<CachingOptions>>().Get(CachingDefaults.DefaultCacheName);
    }
}
