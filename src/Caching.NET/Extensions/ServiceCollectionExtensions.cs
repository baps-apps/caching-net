using System.Diagnostics.CodeAnalysis;
using Caching.NET.Configuration;
using Caching.NET.Health;
using Caching.NET.Internal;
using Caching.NET.Keys;
using Caching.NET.Options;
using Caching.NET.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace Caching.NET.Extensions;

/// <summary>
/// Registers Caching.NET. These are the only cache-registration calls an application makes: the
/// cache engine, its serializer, its Redis connection, its backplane and its telemetry are all
/// wired internally.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string ConfigurationBindingWarning =
        "Binding CachingOptions from IConfiguration uses reflection. For a trimmed or native-AOT "
        + "application, register the cache from code with AddCaching(Action<CachingBuilder>) or "
        + "AddCachingOptions(Action<CachingOptions>) instead.";

    /// <summary>
    /// Registers the default cache from the <c>CacheOptions</c> configuration section, plus any
    /// caches declared under <c>CacheOptions:NamedCaches</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, or the <c>CacheOptions</c> section itself.</param>
    /// <example>
    /// <code><![CDATA[
    /// builder.Services.AddCaching(builder.Configuration);
    /// // or, passing the section directly:
    /// builder.Services.AddCaching(builder.Configuration.GetSection("CacheOptions"));
    /// ]]></code>
    /// </example>
    [RequiresDynamicCode(ConfigurationBindingWarning)]
    [RequiresUnreferencedCode(ConfigurationBindingWarning)]
    public static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
        => services.AddCaching(configuration, configure: null);

    /// <summary>
    /// Registers the default cache from configuration with fluent overrides applied on top.
    /// Fluent settings win over bound values.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration, or the <c>CacheOptions</c> section itself.</param>
    /// <param name="configure">Fluent overrides. May be <c>null</c>.</param>
    [RequiresDynamicCode(ConfigurationBindingWarning)]
    [RequiresUnreferencedCode(ConfigurationBindingWarning)]
    public static IServiceCollection AddCaching(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<CachingBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = ResolveSection(configuration);
        AddCache(services, CachingDefaults.DefaultCacheName, isDefault: true, Binder(section), configure);
        AddNamedCachesFromConfiguration(services, section);
        return services;
    }

    /// <summary>
    /// Registers the default cache entirely from code.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Fluent configuration.</param>
    /// <example>
    /// <code><![CDATA[
    /// builder.Services.AddCaching(cache => cache
    ///     .UseHybrid(redisConnectionString)
    ///     .WithApplicationPrefix("orders-api")
    ///     .WithDefaultExpiration(TimeSpan.FromMinutes(10)));
    /// ]]></code>
    /// </example>
    public static IServiceCollection AddCaching(this IServiceCollection services, Action<CachingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        AddCache(services, CachingDefaults.DefaultCacheName, isDefault: true, bind: null, configure);
        return services;
    }

    /// <summary>
    /// Registers the default cache from a strongly typed options delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Receives the options instance.</param>
    /// <example>
    /// <code><![CDATA[
    /// builder.Services.AddCachingOptions(options =>
    /// {
    ///     options.Mode = CacheMode.Hybrid;
    ///     options.ApplicationPrefix = "orders-api";
    ///     options.Redis.Configuration = redisConnectionString;
    /// });
    /// ]]></code>
    /// </example>
    public static IServiceCollection AddCachingOptions(
        this IServiceCollection services,
        Action<CachingOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        return services.AddCaching(builder => configureOptions(builder.Options));
    }

    /// <summary>
    /// Registers an additional named cache, resolvable with <c>[FromKeyedServices(name)]</c> or
    /// <c>ICacheProvider.GetCache(name)</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="cacheName">Unique cache name. Registering the same name twice throws at startup.</param>
    /// <param name="configure">Fluent configuration for this cache.</param>
    /// <example>
    /// <code><![CDATA[
    /// builder.Services.AddCaching("short-lived", cache => cache
    ///     .UseInMemory()
    ///     .WithApplicationPrefix("orders-api")
    ///     .WithDefaultExpiration(TimeSpan.FromSeconds(30)));
    /// ]]></code>
    /// </example>
    public static IServiceCollection AddCaching(
        this IServiceCollection services,
        string cacheName,
        Action<CachingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);
        ArgumentNullException.ThrowIfNull(configure);

        AddCache(services, cacheName, isDefault: false, bind: null, configure);
        return services;
    }

    /// <summary>
    /// Registers an additional named cache from a configuration section, with optional fluent overrides.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="cacheName">Unique cache name.</param>
    /// <param name="configuration">The section holding this cache's settings.</param>
    /// <param name="configure">Fluent overrides. May be <c>null</c>.</param>
    [RequiresDynamicCode(ConfigurationBindingWarning)]
    [RequiresUnreferencedCode(ConfigurationBindingWarning)]
    public static IServiceCollection AddCaching(
        this IServiceCollection services,
        string cacheName,
        IConfiguration configuration,
        Action<CachingBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheName);
        ArgumentNullException.ThrowIfNull(configuration);

        AddCache(services, cacheName, isDefault: false, Binder(configuration), configure);
        return services;
    }

    /// <summary>
    /// Adds Caching.NET health checks to an existing health-checks builder.
    /// </summary>
    /// <param name="builder">The health-checks builder.</param>
    /// <param name="name">Base health-check name.</param>
    /// <param name="failureStatus">Status reported on failure.</param>
    /// <param name="splitLivenessReadiness">
    /// Register a liveness check (no I/O) and a readiness check (real round trip) under
    /// <c>{name}-liveness</c> and <c>{name}-readiness</c>, tagged <c>liveness</c> and <c>readiness</c>.
    /// </param>
    public static IHealthChecksBuilder AddCachingHealthChecks(
        this IHealthChecksBuilder builder,
        string name = "caching-net",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        bool splitLivenessReadiness = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (splitLivenessReadiness)
        {
            return builder
                .AddCheck<CachingLivenessHealthCheck>($"{name}-liveness", failureStatus, ["liveness"])
                .AddCheck<CachingHealthCheck>($"{name}-readiness", failureStatus, ["readiness"]);
        }

        return builder.AddCheck<CachingHealthCheck>(name, failureStatus);
    }

    /// <summary>
    /// Resolves every registered cache so that a dependency-injection or configuration mistake
    /// fails immediately rather than on the first cache call.
    /// </summary>
    /// <param name="serviceProvider">The built service provider.</param>
    public static IServiceProvider ValidateCachingRegistration(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var provider = serviceProvider.GetRequiredService<ICacheProvider>();
        foreach (var cacheName in provider.CacheNames)
        {
            _ = provider.GetCache(cacheName);
        }

        return serviceProvider;
    }

    private static void AddCache(
        IServiceCollection services,
        string cacheName,
        bool isDefault,
        Action<OptionsBuilder<CachingOptions>>? bind,
        Action<CachingBuilder>? configure)
    {
        CacheRegistrationTracker.ForServices(services).Claim(cacheName, isDefault);

        services.AddOptions();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<CachingOptions>, CachingOptionsValidator>());

        var optionsBuilder = services.AddOptions<CachingOptions>(cacheName);

        bind?.Invoke(optionsBuilder);

        // The cache name always comes from the registration, never from configuration, so a
        // copy-pasted appsettings block cannot silently retarget a named cache.
        optionsBuilder.Configure(options => options.CacheName = cacheName);

        if (configure is not null)
        {
            // PostConfigure runs after Bind, so fluent settings win over configuration.
            optionsBuilder.PostConfigure(options =>
            {
                var builder = new CachingBuilder(options);
                configure(builder);
                options.CacheName = cacheName;
            });
        }

        optionsBuilder.ValidateOnStart();

        services.AddKeyedSingleton(cacheName, (sp, key) => CacheEngineFactory.Create(sp, (string)key!));
        services.AddKeyedSingleton<IFusionCache>(
            cacheName,
            static (sp, key) => sp.GetRequiredKeyedService<CacheInstance>(key).Cache);
        services.AddKeyedSingleton<ICacheGuard>(
            cacheName,
            static (sp, key) => sp.GetRequiredKeyedService<CacheInstance>(key).Guard);

        services.AddSingleton(sp => new CacheRegistration(
            cacheName,
            isDefault,
            () => sp.GetRequiredKeyedService<CacheInstance>(cacheName)));

        if (isDefault)
        {
            // Resolved through CacheInstance rather than through the keyed IFusionCache so the two
            // registrations can never form a resolution cycle.
            services.TryAddSingleton<IFusionCache>(sp => sp.GetRequiredKeyedService<CacheInstance>(cacheName).Cache);
            services.TryAddSingleton<ICacheGuard>(sp => sp.GetRequiredKeyedService<CacheInstance>(cacheName).Guard);
        }

        services.TryAddSingleton<ICacheProvider, CacheProvider>();
        services.TryAddSingleton<ICacheKeyFactory, DefaultCacheKeyFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CachingStartupService>());

        if (configure is null)
        {
            return;
        }

        // Health-check registration is a service-collection concern, not an options concern, so the
        // builder is replayed once here against a throwaway options instance to read the intent.
        var probe = new CachingBuilder(new CachingOptions());
        configure(probe);
        if (probe.RegisterHealthChecks)
        {
            services.AddHealthChecks().AddCachingHealthChecks(
                probe.HealthCheckName,
                splitLivenessReadiness: probe.HealthCheckSplit);
        }
    }

    // Configuration binding is the only reflection-based step in registration. Handing it to
    // AddCache as a delegate built by an annotated caller keeps the code-first overloads free of the
    // warning, so a trimmed or native-AOT application is told exactly which entry points cost it.
    [RequiresDynamicCode(ConfigurationBindingWarning)]
    [RequiresUnreferencedCode(ConfigurationBindingWarning)]
    private static Action<OptionsBuilder<CachingOptions>> Binder(IConfiguration section)
        => optionsBuilder => optionsBuilder.Bind(section);

    [RequiresDynamicCode(ConfigurationBindingWarning)]
    [RequiresUnreferencedCode(ConfigurationBindingWarning)]
    private static void AddNamedCachesFromConfiguration(IServiceCollection services, IConfiguration section)
    {
        var namedCaches = section.GetSection(CacheConfigurationKeys.NamedCaches);
        if (!namedCaches.Exists())
        {
            return;
        }

        foreach (var child in namedCaches.GetChildren())
        {
            AddCache(services, child.Key, isDefault: false, Binder(child), configure: null);
        }
    }

    // Accepts either the application root configuration or the Caching section itself, so
    // AddCaching(builder.Configuration) and AddCaching(section) both behave.
    private static IConfiguration ResolveSection(IConfiguration configuration)
    {
        if (configuration is IConfigurationSection candidate
            && string.Equals(candidate.Key, CacheConfigurationKeys.CacheOptions, StringComparison.Ordinal))
        {
            return candidate;
        }

        var section = configuration.GetSection(CacheConfigurationKeys.CacheOptions);
        return section.Exists() ? section : configuration;
    }
}
