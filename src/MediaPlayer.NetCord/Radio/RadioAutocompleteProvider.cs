using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace MediaPlayer.NetCord.Radio;

public sealed class RadioAutocompleteProvider(RadioSourceService radioSourceService)
    : IAutocompleteProvider<AutocompleteInteractionContext>
{
    private const int MaxChoices = 10;

    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value?.ToString();
        var radios = radioSourceService.GetRadios();

        var choices = radios
            .Where(radio => string.IsNullOrWhiteSpace(input) ||
                            radio.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(MaxChoices)
            .Select(static radio => new ApplicationCommandOptionChoiceProperties(radio.Name, radio.Name));

        return new ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?>(choices);
    }
}
