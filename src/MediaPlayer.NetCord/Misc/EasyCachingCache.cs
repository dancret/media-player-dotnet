using EasyCaching.Core;
using MediaPlayer.Tracks;
using Microsoft.Extensions.Logging;

namespace MediaPlayer.NetCord.Misc
{
    /// <summary>
    /// Thin wrapper around EasyCaching to provide strongly-typed caching with logging.
    /// https://easycaching.readthedocs.io/en/latest/Features/
    /// </summary>
    /// <param name="provider">Instance of <see cref="IEasyCachingProvider"/>.</param>
    public sealed class EasyCachingCache(ILogger<EasyCachingCache> logger, IEasyCachingProvider provider) : ITrackRequestCache
    {
        public async ValueTask<IReadOnlyList<Track>?> TryGetAsync(string key, CancellationToken ct = default)
        {
            try
            {
                var value = await provider.GetAsync<List<Track>>(key, ct);

                return value.Value;
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Failed to find {key}.");
                return null;
            }
        }

        public async ValueTask SetAsync(string key, IReadOnlyList<Track> tracks, TimeSpan ttl, CancellationToken ct = default)
        {
            try
            {
                if (!tracks.Any()) return;

                await provider.SetAsync(key, tracks.ToList(), ttl, ct);
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Failed to set {key}.");
            }
        }
    }
}
