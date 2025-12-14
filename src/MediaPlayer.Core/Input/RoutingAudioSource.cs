using MediaPlayer.Tracks;

namespace MediaPlayer.Input;

/// <summary>
/// Routes <see cref="Track"/>s to different underlying <see cref="IAudioSource"/> instances
/// based on a key (typically the track's TrackInput enum).
/// </summary>
public sealed class RoutingAudioSource : IAudioSource
{
    private readonly Func<Track, TrackInput> _inputSelector;
    private readonly IReadOnlyDictionary<TrackInput, IAudioSource> _sources;
    private readonly IAudioSource? _fallbackSource;

    /// <summary>
    /// Creates a new <see cref="RoutingAudioSource"/>.
    /// </summary>
    /// <param name="inputSelector">
    /// Function that extracts the routing key (e.g., TrackInput) from a <see cref="Track"/>.
    /// </param>
    /// <param name="sources">
    /// Map from the routing key (e.g., TrackInput.LocalFile, TrackInput.YouTube) to the
    /// corresponding <see cref="IAudioSource"/> that knows how to handle such tracks.
    /// </param>
    /// <param name="fallbackSource">
    /// Optional fallback source used when no specific source is registered for the
    /// routing key. If null, an unsupported track type will cause a <see cref="NotSupportedException"/>.
    /// </param>
    public RoutingAudioSource(
        Func<Track, TrackInput> inputSelector,
        IReadOnlyDictionary<TrackInput, IAudioSource> sources,
        IAudioSource? fallbackSource = null)
    {
        _inputSelector = inputSelector ?? throw new ArgumentNullException(nameof(inputSelector));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _fallbackSource = fallbackSource;
    }

    /// <inheritdoc/>
    public async Task<IAudioTrackReader> OpenReaderAsync(Track track, CancellationToken ct)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));

        var key = _inputSelector(track);

        if (_sources.TryGetValue(key, out var source))
        {
            return await source.OpenReaderAsync(track, ct).ConfigureAwait(false);
        }

        if (_fallbackSource is not null)
        {
            return await _fallbackSource.OpenReaderAsync(track, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException(
            $"No audio source registered for track input '{key}'.");
    }
}
