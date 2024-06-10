using System.Text.Json.Nodes;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;
using NodaTime;
using NodaTime.Text;

namespace Nixill.Discord.Countdown;

public static class SaveAndLoad
{
  public static async Task LoadFromFiles()
  {
    LetterFunctions.ValidWords = new(File.ReadAllLines("data/words.txt"));

    List<DeletionTask> tasks = new();

    Instant now = SystemClock.Instance.GetCurrentInstant();
    JsonArray gamesList = (JsonArray)JsonNode.Parse(File.ReadAllText("data/games.json"));
    foreach (JsonObject gameObject in gamesList.Cast<JsonObject>())
    {
      var parsedLastActivity = InstantPattern.ExtendedIso.Parse((string)gameObject["lastActivity"]);
      if (!parsedLastActivity.Success) continue;
      Instant lastActivity = parsedLastActivity.Value;

      try
      {
        DiscordChannel thread = await CountdownBotMain.Discord.GetChannelAsync((ulong)gameObject["thread"]);

        CountdownGame game = new((ulong)gameObject["host"], (DiscordThreadChannel)thread);
        game.ActivePlayers.Clear(); // If the host is already part of the game, they'll be added again next line.
        foreach (ulong player in ((JsonArray)gameObject["players"]).Select(x => (ulong)x)) game.ActivePlayers.Add(player);
        foreach (ulong player in ((JsonArray)gameObject["controllers"]).Select(x => (ulong)x)) game.RoundControllers.Add(player);

        foreach (JsonObject obj in ((JsonArray)gameObject["rounds"]).Cast<JsonObject>())
        {
          DeserializeEventArgs<CountdownRound> roundArgs = new(obj, game);
          CountdownRound.OnDeserialize(roundArgs);
          // game.Rounds.Add(roundArgs.Result);
          // Actually, the above happens in the constructor.
        }

        game.LastActivity = lastActivity;
        tasks.Add(new() { QueuedAt = lastActivity, Game = game });
        CountdownGameController.GamesInProgress[game.ThreadId] = game;
      }
      catch (NotFoundException)
      {
        // do nothing and move on to the next game
      }
    }

    DeleteQueue.Clear();
    DeleteQueue.AddAll(tasks.OrderBy(x => x.QueuedAt));
  }

  internal static async Task OnGuildDownload(DiscordClient sender, GuildDownloadCompletedEventArgs args)
  {
    await LoadFromFiles();
    Task _ = Task.Run(DeleteQueue.Begin);
  }

  public static void SaveToFiles()
  {
    File.WriteAllLines("data/words.txt", LetterFunctions.ValidWords);

    JsonArray gamesList = new();
    foreach (CountdownGame game in CountdownGameController.GamesInProgress.Values)
    {
      JsonObject gameObject = game.Serialize();
      gamesList.Add(gameObject);
    }
    File.WriteAllText("data/games.json", gamesList.ToString());
  }
}

public class QuitCommandClass
{
  [Command("quit")]
  public async Task QuitBot(SlashCommandContext ctx)
  {
    if (ctx.User.Id != CountdownBotMain.OwnerID)
    {
      await ctx.RespondAsync("Only the bot's owner may quit!", true);
      return;
    }

    await ctx.RespondAsync("Shutting down!", true);
    CountdownBotMain.QuitTokenSource.Cancel();
  }
}