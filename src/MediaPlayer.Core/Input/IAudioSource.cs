using MediaPlayer.Tracks;

namespace MediaPlayer.Input;

/// <summary>
/// Represents an audio source that provides functionality to open and manage PCM readers for audio tracks.
/// </summary>
/// <remarks>
/// The <see cref="MediaPlayer.Input.IAudioSource"/> interface defines methods for handling audio tracks
/// and creating readers to process PCM-encoded audio data. Implementations of this interface are expected
/// to support asynchronous operations and proper resource management, including asynchronous disposal.
/// </remarks>
public interface IAudioSource
{
    /// <summary>
    /// Opens a new <see cref="IAudioTrackReader"/> for the specified track.
    /// </summary>
    Task<IAudioTrackReader> OpenReaderAsync(Track track, CancellationToken ct);
}
