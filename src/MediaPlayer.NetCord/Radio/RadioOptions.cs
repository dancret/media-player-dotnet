namespace MediaPlayer.NetCord.Radio;

public sealed record RadioOptions
{
    public Dictionary<string, string> Stations { get; set; } = [];
}
