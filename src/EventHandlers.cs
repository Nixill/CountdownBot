using DSharpPlus;
using DSharpPlus.Commands;

namespace Nixill.Discord.Countdown;

public static class EventHandlers
{
  public static void RegisterEvents(DiscordClient discord, CommandsExtension commands)
  {
    discord.GuildDownloadCompleted += SaveAndLoad.OnGuildDownload;

    discord.ComponentInteractionCreated += GameCommandEvents.JoinButtonPressed;
    discord.ComponentInteractionCreated += GameCommandEvents.LeaveButtonPressed;
    discord.ComponentInteractionCreated += LetterButtonEvents.DrawConsonantButtonPressed;
    discord.ComponentInteractionCreated += LetterButtonEvents.DrawVowelButtonPressed;
    discord.ComponentInteractionCreated += LetterButtonEvents.ShowWordsButtonPressed;
    discord.ComponentInteractionCreated += NumberButtonEvents.PickTargetButtonPressed;
    discord.ComponentInteractionCreated += NumberButtonEvents.PickNumbersButtonPressed;

    commands.CommandErrored += CountdownBotMain.OnCommandErrored;

    CountdownRound.DeserializeEvent += LettersRound.Deserialize;
    CountdownRound.DeserializeEvent += NumbersRound.Deserialize;
  }
}

