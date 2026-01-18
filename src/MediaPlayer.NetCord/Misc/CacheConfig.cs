using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MediaPlayer.NetCord.Misc;

public static class CacheConfig
{
    /// <summary>
    /// Configures the caching providers for track requests.
    /// </summary>
    /// <remarks>
    /// The method checks the configuration for EasyCaching settings and sets up the appropriate caching provider.
    /// See documentation at:
    /// <list type="bullet">
    ///   <item>
    ///     <description>In memory app settings: <see href="https://easycaching.readthedocs.io/en/latest/In-Memory/"/>.</description>
    ///   </item>
    ///   <item>
    ///     <description>Redis app settings: <see href="https://easycaching.readthedocs.io/en/latest/Redis/"/>.</description>
    ///   </item>
    ///   <item>
    ///     <description>SQLite app settings: <see href="https://easycaching.readthedocs.io/en/latest/SQLite/"/>.</description>
    ///   </item>
    /// </list>
    /// </remarks>
    /// <param name="services">Runtime services collection.</param>
    /// <param name="configuration">Configuration settings for the current runtime.</param>
    public static void Configure(IServiceCollection services, ConfigurationManager configuration)
    {
        var easyCachingSection = configuration.GetSection("EasyCaching");
        var inMemorySection = easyCachingSection.GetSection("InMemory");
        var redisSection = easyCachingSection.GetSection("Redis");
        var sqliteSection = easyCachingSection.GetSection("sqlite");

        if (redisSection.Exists())
        {
            services.AddEasyCaching(options =>
            {
                options.UseRedis(configuration, "defaultRedis");
                options.WithJson();
            });
        }
        else if (sqliteSection.Exists())
        {
            services.AddEasyCaching(options =>
            {
                options.UseSQLite(configuration, "default");
            });
        }
        else if (inMemorySection.Exists())
        {
            services.AddEasyCaching(options =>
            {
                options.UseInMemory(configuration, "default");
            });
        }
        else
        {
            services.AddEasyCaching(options =>
            {
                options.UseInMemory("default");
            });
        }
    }
}