using Microsoft.Extensions.Options;

namespace MediaPlayer.NetCord.Radio;

public sealed class RadioSourceService
{
    private readonly IReadOnlyList<RadioEntry> _radios;
    private readonly Dictionary<string, RadioEntry> _radiosByName;

    public RadioSourceService(IOptions<RadioOptions> options)
    {
        var entries = options.Value.Stations
            .Select(static station => new RadioEntry(station.Key, station.Value))
            .OrderBy(static radio => radio.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _radios = entries;
        _radiosByName = entries.ToDictionary(
            static radio => radio.Name,
            static radio => radio,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RadioEntry> GetRadios() => _radios;

    public bool TryGetRadio(string name, out RadioEntry radio)
        => _radiosByName.TryGetValue(name, out radio!);
}
