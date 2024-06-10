using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json.Nodes;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;
using Nixill.Collections;
using Nixill.Utils;
using NodaTime;
using NodaTime.Text;
using Quartz.Impl.Triggers;

namespace Nixill.Discord.Countdown;

// Note: A round can be anywhere from 2 to 8 on this list.
public enum GameState
{
  None = 0,
  // 0 should never be used.
  Roundless = 1, // The game has just been opened. No rounds have started yet.
  // 1 -> 2
  Setup = 2, // A round has just been opened, and is in setup.
  // 2 -> 6
  // there were 3-5 but I removed them
  Declarations = 6, // The players have been asked to declare. None have yet.
  // 6 -> 7
  DeclarationsMade = 7, // Some, but not all, players have declared.
  // 7 -> 8
  RoundEnded = 8, // All declarations are complete and the round has ended.
  // 8 -> 2 or 9
  GameEnded = 9 // The game is over.
  // 9 cannot be changed
}

public static class CountdownGameController
{
  internal static ConcurrentDictionary<ulong /*Thread*/, CountdownGame> GamesInProgress = new();

  internal static bool IsGameThread(ulong threadId)
  {
    return GamesInProgress.ContainsKey(threadId);
  }

  internal static CountdownGame GetOrNull(ulong threadId)
    => GamesInProgress.GetValueOrDefault(threadId);

  internal static bool IsGameHost(DiscordInteraction intr)
  {
    ulong threadId = intr.Channel.Id;
    if (!IsGameThread(threadId)) return false;
    CountdownGame game = GamesInProgress[threadId];
    return intr.User.Id == game.Host;
  }

  public static Task DeleteOldGames()
  {
    var now = CountdownBotMain.Now;
    var day = Duration.FromDays(1);
    foreach (var key in GamesInProgress.Where(g => g.Value.LastActivity + day < now).Select(x => x.Key))
    {
      GamesInProgress.TryRemove(key, out _);
    }
    return Task.CompletedTask;
  }
}

public class CountdownGame
{
  internal HashSet<ulong> ActivePlayers = new();
  internal List<CountdownRound> Rounds = new();
  public GameState State { get; internal set; } = GameState.Roundless;
  public ulong Host { get; internal set; }
  public readonly ulong ThreadId;
  public DiscordThreadChannel Thread { get; private set; }
  internal CountdownRound LatestRound => Rounds.LastOrDefault();
  public Instant LastActivity { get; internal set; }

  internal List<ulong> RoundControllers = new();
  private Random randomizer = new();
  public ulong PickController()
  {
    RoundControllers.AddRange(ActivePlayers);
    ulong Controller = RoundControllers.Where(x => ActivePlayers.Contains(x)).OrderBy(x => randomizer.Next()).FirstOrDefault();
    RoundControllers.RemoveAll(x => x == Controller);
    return Controller;
  }

  internal void NoteActivity()
  {
    LastActivity = SystemClock.Instance.GetCurrentInstant();
    DeleteQueue.Add(this);
  }

  public CountdownGame(ulong host, ulong thread)
  {
    Host = host;
    ThreadId = thread;
    ActivePlayers.Add(Host);
    NoteActivity();
    Task.Run(async () => Thread = await CountdownBotMain.Discord.GetChannelAsync(ThreadId) as DiscordThreadChannel);
  }

  public CountdownGame(ulong host, DiscordThreadChannel thread)
  {
    Host = host;
    Thread = thread;
    ThreadId = thread.Id;
    ActivePlayers.Add(Host);
    NoteActivity();
  }

  public JsonObject Serialize()
  {
    JsonObject gameObject = new();
    gameObject["players"] = new JsonArray(ActivePlayers.Select(x => JsonValue.Create(x)).ToArray());
    gameObject["host"] = Host;
    gameObject["thread"] = ThreadId;
    gameObject["state"] = State.ToString();
    gameObject["controllers"] = new JsonArray(RoundControllers.Select(x => JsonValue.Create(x)).ToArray());
    gameObject["lastActivity"] = InstantPattern.ExtendedIso.Format(LastActivity);

    JsonArray gameRounds = new();
    foreach (CountdownRound round in Rounds)
    {
      gameRounds.Add(round.Serialize());
    }

    gameObject["rounds"] = gameRounds;

    return gameObject;
  }

  public Stream Export()
  {
    Stream file = new MemoryStream();
    StreamWriter writer = new StreamWriter(file);
    writer.Write(Serialize().ToString());
    writer.Flush();
    file.Position = 0;

    return file;
  }

  public async Task HandleEndOfGame()
  {
    DictionaryGenerator<ulong, int> soloScores = new();
    DictionaryGenerator<ulong, int> vsScores = new();

    foreach (CountdownRound round in Rounds)
    {
      foreach (var entry in round.Scores)
      {
        soloScores[entry.Player] += entry.Score;
      }

      foreach (var entry in round.Scores.MaxManyBy(x => x.Priority))
      {
        vsScores[entry.Player] += entry.Score;
      }
    }

    State = GameState.GameEnded;

    try
    {
      var msg = await Thread.SendMessageAsync(
        "The game is over! The scores are as follows:",
        new DiscordEmbedBuilder()
          .AddField("Vs scores:", (vsScores.Any()) ? string.Join("\n", vsScores
            .Select(vsc => $"<@{vsc.Key}>: {vsc.Value}")) : "(None!)")
          .AddField("Solo scores:", (soloScores.Any()) ? string.Join("\n", soloScores
            .Select(vsc => $"<@{vsc.Key}>: {vsc.Value}")) : "(None!)"));

      await msg.RespondAsync(new DiscordMessageBuilder()
        .WithContent("The following export can be given back to the bot to continue the game later:")
        .AddFile($"game-{Thread.Id}.json", Export(), AddFileOptions.None)
      );
    }
    catch (UnauthorizedException)
    {
      // do nothing
    }
    catch (NotFoundException)
    {
      // do nothing here either
    }
  }
}

public abstract class CountdownRound
{
  public readonly CountdownGame Game;
  public abstract IEnumerable<(ulong Player, int Score, int Priority)> Scores { get; }
  public GameState State { get; private set; }

  public IEnumerable<ulong> Participants => Players.AsReadOnly();
  protected internal List<ulong> Players = new();
  public readonly ulong Controller;

  public CountdownRound(CountdownGame game, ulong controller = 0)
  {
    Game = game;
    Players = new(game.ActivePlayers);
    if (controller == 0) Controller = game.PickController();
    else Controller = controller;

    game.Rounds.Add(this);
    SetState(GameState.Setup);
  }

  protected internal abstract JsonObject Serialize();
  protected internal static event EventHandler<DeserializeEventArgs<CountdownRound>> DeserializeEvent;

  protected internal void SetState(GameState newState)
  {
    State = newState;
    Game.State = newState;
    Game.NoteActivity();
  }

  internal static void OnDeserialize(DeserializeEventArgs<CountdownRound> args)
  {
    DeserializeEvent.Invoke(null, args);
  }

  protected internal abstract ValueTask<bool> HandleDeparture(ulong player);
}

public class DeserializeEventArgs<T>
{
  public JsonObject Object;
  public T Result = default(T);
  public bool Success = false;
  public CountdownGame Game;

  public DeserializeEventArgs(JsonObject obj, CountdownGame game)
  {
    Game = game;
    Object = obj;
  }
}

public static class GameInteractionLogic
{
  public static async Task JoinGame(DiscordInteraction intr)
  {
    CountdownGame game = CountdownGameController.GetOrNull(intr.Channel.Id);
    if (game == null)
    {
      await intr.EphemeralResponse("This isn't a game thread!");
      return;
    }

    if (game.State == GameState.GameEnded)
    {
      await intr.EphemeralResponse("This game has already ended!");
      return;
    }

    var players = game.ActivePlayers;
    if (players.Contains(intr.User.Id))
    {
      await intr.EphemeralResponse("You're already in this game!");
    }
    else
    {
      players.Add(intr.User.Id);
      await intr.RegularResponse($"**{intr.User.Mention}** has joined this game! (Now **{players.Count}** players.)");

      if (game.State == GameState.Setup || game.State == GameState.Declarations)
      {
        game.LatestRound.Players.Add(intr.User.Id);
        await intr.EphemeralFollowup("You've been added to the active round.");
      }

      else if (game.State == GameState.DeclarationsMade)
      {
        await intr.EphemeralFollowup("You could not join the active round. Your participation begins in the next round.");
      }
    }
  }

  public static async Task LeaveGame(DiscordInteraction intr)
  {
    CountdownGame game = CountdownGameController.GetOrNull(intr.Channel.Id);
    if (game == null)
    {
      await intr.EphemeralResponse("This isn't a game thread!");
      return;
    }

    if (game.State == GameState.GameEnded)
    {
      await intr.EphemeralResponse("This game has already ended!");
      return;
    }

    var players = game.ActivePlayers;
    if (!players.Contains(intr.User.Id))
    {
      await intr.EphemeralResponse("You're not in this game!");
    }
    else
    {
      players.Remove(intr.User.Id);
      await intr.RegularResponse($"**{intr.User.Mention}** has left this game! (Now **{players.Count}** players.)");

      if (game.State == GameState.Setup || game.State == GameState.Declarations)
      {
        game.LatestRound.Players.Remove(intr.User.Id);
        await intr.EphemeralFollowup("You've been removed from the active round.");
      }

      else if (game.State == GameState.DeclarationsMade)
      {
        if (await game.LatestRound.HandleDeparture(intr.User.Id))
        {
          await intr.EphemeralFollowup("You've been removed from the active round.");
        }
        else
        {
          await intr.EphemeralFollowup("You couldn't be removed from the active round.");
        }
      }
    }
  }
}

[Command("game")]
[Description("Create and manage Countdown games.")]
public class GameCommands
{
  [Command("create")]
  public async Task CreateGame(SlashCommandContext ctx,
    [Description("Title of the thread")] string title = null)
  {
    if (title == null)
    {
      title = $"Countdown Game {ctx.Interaction.Id}";
    }

    if (ctx.Channel.IsPrivate)
    {
      await ctx.RespondAsync("Games can only be started in servers!");
      return;
    }

    if (ctx.Channel.IsThread && CountdownGameController.GamesInProgress.ContainsKey(ctx.Channel.Id))
    {
      await ctx.RespondAsync("A game is already occurring in this thread!", true);
      return;
    }

    await ctx.DeferResponseAsync(true);

    DiscordThreadChannel thread = null;

    if (!ctx.Channel.IsThread)
    {
      try
      {
        thread = await ctx.Channel.CreateThreadAsync(title, DiscordAutoArchiveDuration.Day, DiscordChannelType.PublicThread, "Created for a Countdown game.");
      }
      catch (Exception e)
      {
        await ctx.EditResponseAsync($"Could not create a thread for a Countdown game ({e.ToString()})");
        return;
      }
    }
    else
    {
      thread = ctx.Channel as DiscordThreadChannel;
    }

    await ctx.DeleteResponseAsync();

    var joinButton = new DiscordButtonComponent(DiscordButtonStyle.Primary, $"game-join", "Join");
    var leaveButton = new DiscordButtonComponent(DiscordButtonStyle.Secondary, $"game-leave", "Leave");

    var gameStartMessage = new DiscordMessageBuilder()
      .WithContent("A Countdown game is starting in this thread. Click 'Join' to join!")
      .AddComponents(joinButton, leaveButton);

    await thread.SendMessageAsync(gameStartMessage);

    CountdownGame game = new(ctx.User.Id, thread.Id);
    CountdownGameController.GamesInProgress[thread.Id] = game;
  }

  [Command("join")]
  public async Task JoinGame(SlashCommandContext ctx)
    => await GameInteractionLogic.JoinGame(ctx.Interaction);

  [Command("leave")]
  public async Task LeaveGame(SlashCommandContext ctx)
    => await GameInteractionLogic.LeaveGame(ctx.Interaction);

  [Command("host")]
  public async Task GameHostTransfer(SlashCommandContext ctx,
    [Description("The user to make the new host.")] DiscordUser newHost
  )
  {
    CountdownGame game = CountdownGameController.GetOrNull(ctx.Channel.Id);
    if (game == null)
    {
      await ctx.RespondAsync("This isn't a game thread!", true);
      return;
    }
    if (game.State == GameState.GameEnded)
    {
      await ctx.RespondAsync("This game has already ended!", true);
      return;
    }
    if (!CountdownGameController.IsGameHost(ctx.Interaction))
    {
      await ctx.RespondAsync("You're not the current host of this game!", true);
      return;
    }

    game.Host = newHost.Id;
    await ctx.RespondAsync($"{newHost.Mention} is now the host of this game!");
  }

  [Command("end")]
  public async Task EndGame(SlashCommandContext ctx)
  {
    CountdownGame game = CountdownGameController.GetOrNull(ctx.Channel.Id);
    if (game == null)
    {
      await ctx.RespondAsync("This isn't a game thread!", true);
      return;
    }
    if (!CountdownGameController.IsGameHost(ctx.Interaction))
    {
      await ctx.RespondAsync("You're not the current host of this game!", true);
      return;
    }

    await ctx.RespondAsync("Ending game.", true);
    await game.HandleEndOfGame();
  }

  [Command("export")]
  [Description("Export the current game to save for later.")]
  public async Task DownloadGame(SlashCommandContext ctx)
  {
    if (!Condition.Check(ctx.Interaction, Condition.IsGameThread,
      out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    var msg = new DiscordFollowupMessageBuilder().AddFile("game.json", result.Game.Export(), AddFileOptions.None).AsEphemeral();

    await ctx.RespondAsync(msg);
  }
}

public static class GameCommandEvents
{
  public static async Task JoinButtonPressed(DiscordClient discord, ComponentInteractionCreateEventArgs args)
  {
    if (args.Id != "game-join") return;
    await GameInteractionLogic.JoinGame(args.Interaction);
  }

  public static async Task LeaveButtonPressed(DiscordClient discord, ComponentInteractionCreateEventArgs args)
  {
    if (args.Id != "game-leave") return;
    await GameInteractionLogic.LeaveGame(args.Interaction);
  }
}