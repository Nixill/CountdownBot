using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;

namespace Nixill.Discord.Countdown;

public static class Send
{
  public async static Task EphemeralResponse(this DiscordInteraction intr, string text)
    => await intr.CreateResponseAsync(DiscordInteractionResponseType.ChannelMessageWithSource,
      new DiscordInteractionResponseBuilder().WithContent(text).AsEphemeral());

  public async static Task RegularResponse(this DiscordInteraction intr, string text)
    => await intr.CreateResponseAsync(DiscordInteractionResponseType.ChannelMessageWithSource,
      new DiscordInteractionResponseBuilder().WithContent(text));

  public async static Task EphemeralFollowup(this DiscordInteraction intr, string text)
    => await intr.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder().WithContent(text).AsEphemeral());

  public async static Task RegularFollowup(this DiscordInteraction intr, string text)
    => await intr.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder().WithContent(text));

}