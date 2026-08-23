namespace MediaPlayer;

/// <summary>
/// Specifies the input source type for a media track.
/// </summary>
public enum TrackInput
{
    /// <summary>
    /// Indicates that the track is sourced from a local file.
    /// </summary>
    LocalFile,
    /// <summary>
    /// Indicates that the track is sourced from a YouTube video.
    /// </summary>
    YouTube,
    /// <summary>
    /// Indicates that the track is sourced from a radio stream URL.
    /// </summary>
    Radio
}

/// <summary>
/// Specifies the repeat mode of the playback loop.
/// </summary>
public enum RepeatMode { None, One, All }

/// <summary>
/// Specifies the current state of the playback loop.
/// </summary>
public enum PlayerState { Idle, Playing, Paused, Stopped }

/// <summary>
/// Defines the reasons why playback has ended.
/// </summary>
public enum PlaybackEndReason { Completed, Cancelled, Failed }
