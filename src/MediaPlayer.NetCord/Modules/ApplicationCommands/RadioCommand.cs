using MediaPlayer.NetCord.Player;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using System.Diagnostics.CodeAnalysis;
using MediaPlayer.NetCord.Radio;

namespace MediaPlayer.NetCord.Modules.ApplicationCommands;

[SuppressMessage("ReSharper", "UnusedMember.Global", Justification = "Called via reflection")]
[SuppressMessage("ReSharper", "UnusedType.Global", Justification = "Called via reflection")]
public class RadioCommand(
    ILogger<RadioCommand> logger,
    NetCordDiscordPlayerProvider playerProvider,
    RadioSourceService radioSourceService)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("radio", "Play a configured radio station", Contexts = [InteractionContextType.Guild])]
    public async Task Radio(
        [SlashCommandParameter(
            Name = "station",
            Description = "Radio station",
            AutocompleteProviderType = typeof(RadioAutocompleteProvider))]
        string station)
    {
        try
        {
            logger.LogInformation(
                "{Command}: in {ChannelName} by {UserUsername} station {Station}.",
                nameof(Radio),
                Context.Channel.ToString(),
                Context.User.Username,
                station);

            await RespondAsync(InteractionCallback.DeferredMessage());

            var discordPlayer = await DiscordCommandHelpers.TryGetPlayerForUserAsync(Context, playerProvider);
            if (discordPlayer is null)
            {
                await ModifyResponseAsync(DiscordCommandHelpers.RespondForNullPlayer);
                return;
            }

            if (!radioSourceService.TryGetRadio(station, out var radio))
            {
                await FollowupAsync($"Radio station '{station}' is not configured.");
                return;
            }

            var track = radio.ToTrack();
            await discordPlayer.PlayNowAsync(track);

            await FollowupAsync($"Playing radio {track.Title}.");
        }
        catch (Exception e)
        {
            logger.LogError(e, nameof(Radio));
            await ModifyResponseAsync(DiscordCommandHelpers.RespondForCommandFailure);
        }
    }
}
