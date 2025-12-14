using MediaPlayer.Ffmpeg;
using MediaPlayer.Tracks;
using Microsoft.Extensions.Logging;

namespace MediaPlayer.Input;

/// <summary>
/// Audio source that reads from local files using ffmpeg to decode into PCM.
/// </summary>
public sealed class LocalFileAudioSource : IAudioSource
{
    private readonly Func<Track, string> _pathSelector;
    private readonly FfmpegPcmSourceOptions _options;
    private readonly ILogger<LocalFileAudioSource> _logger;

    /// <summary>
    /// Creates a new <see cref="LocalFileAudioSource"/>.
    /// </summary>
    /// <param name="logger">
    /// Logger instance.
    /// </param>
    /// <param name="pathSelector">
    /// Function that resolves a <see cref="Track"/> into a filesystem path 
    /// suitable to pass to ffmpeg (absolute or relative).
    /// </param>
    /// <param name="options">
    /// Optional ffmpeg PCM options. If null, defaults (ffmpeg on PATH, 48 kHz, s16le, stereo) are used.
    /// </param>
    public LocalFileAudioSource(
        ILogger<LocalFileAudioSource> logger,
        Func<Track, string> pathSelector,
        FfmpegPcmSourceOptions? options = null)
    {
        _pathSelector = pathSelector ?? throw new ArgumentNullException(nameof(pathSelector));
        _options = options ?? new FfmpegPcmSourceOptions();
        _logger = logger ?? throw new ArgumentNullException();
    }

    /// <inheritdoc/>
    public Task<IAudioTrackReader> OpenReaderAsync(Track track, CancellationToken ct)
    {
        if (track is null) throw new ArgumentNullException(nameof(track));

        var path = _pathSelector(track);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Track path selector returned null or empty path.");

        // Optional: fail fast if the file is missing. If you want to let ffmpeg handle it, you can remove this.
        if (!File.Exists(path))
            throw new FileNotFoundException("Audio file not found for track.", path);

        var reader = FfmpegPcmSource.StartPcmReader(path, _options, _logger, ct);
        return Task.FromResult(reader);
    }
}
