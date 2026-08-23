namespace MediaPlayer.Tracks;

/// <summary>
/// Represents a media track with associated metadata and playback information.
/// </summary>
/// <param name="Uri">
/// The unique resource identifier (URI) of the track. The meaning of this parameter depends on the <see cref="TrackInput"/>:
/// <list type="bullet">
///   <item>
///     <description>For <see cref="TrackInput.LocalFile"/>, this is the file path</description>
///   </item>
///   <item>
///     <description>For <see cref="TrackInput.YouTube"/>, this is the YouTube video URL</description>
///   </item>
///   <item>
///     <description>For <see cref="TrackInput.Radio"/>, this is the radio stream URL</description>
///   </item>
/// </list>
/// </param>
/// <param name="Title">The title or name of the track.</param>
/// <param name="Input">The source type of the track, such as a local file, YouTube, or radio.</param>
/// <param name="DurationHint">An optional hint for the track's duration.</param>
public sealed record Track(
    string Uri,
    string Title,
    TrackInput Input,
    TimeSpan? DurationHint = null
);

/// <summary>
/// Represents a high-level request to resolve an arbitrary input string
/// (URL, file path, etc.) into one or more <see cref="Track"/> instances.
/// </summary>
/// <remarks>
/// A <see cref="TrackRequest"/> describes user intent at a very generic level.
/// It is the responsibility of one or more <see cref="ITrackResolver"/> implementations
/// to interpret the request and return the appropriate tracks.
/// </remarks>
/// <param name="Raw">
/// The raw user input. This can be a URL, a local file path, a search string, etc.
/// </param>
/// <param name="InputHint">
/// Optional hint indicating the expected <see cref="TrackInput"/> type.
/// When provided, resolvers may skip detection and treat this request as a known input type.
/// </param>
public sealed record TrackRequest(
    string Raw,
    TrackInput? InputHint = null
);
