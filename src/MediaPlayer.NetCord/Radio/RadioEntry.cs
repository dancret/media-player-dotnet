using MediaPlayer.Tracks;

namespace MediaPlayer.NetCord.Radio;

public sealed record RadioEntry(string Name, string Uri)
{
    public Track ToTrack() => new(Uri, Name, TrackInput.Radio);
}
