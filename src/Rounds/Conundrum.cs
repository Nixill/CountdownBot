using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using Nixill.Collections;
using Nixill.Utils;

namespace Nixill.Discord.Countdown;

public class ConundrumRound : CountdownRound
{
  internal List<ConundrumSubmission> Submissions = new();
  internal string Answer = "";
  internal string Letters = "";
  internal DiscordMessage Message;
  internal ulong MessageId;

  public ConundrumRound(CountdownGame game, DiscordMessage message) : base(game, 0)
  {
    Message = message;
    MessageId = message?.Id ?? 0;

    SetState(GameState.Declarations);
  }

  public ConundrumRound(CountdownGame game, ulong messageId) : base(game, 0)
  {
    MessageId = messageId;
    if (messageId != 0) Task.Run(async () => Message = await (await CountdownBotMain.Discord.GetChannelAsync(Game.ThreadId)).GetMessageAsync(MessageId));

    SetState(GameState.Declarations);
  }

  public override IEnumerable<(ulong Player, int Score, int Priority)> Scores =>
    Submissions.Select(x => (x.Word == Answer) ? (x.User, 10, 1) : (x.User, 0, 0));

  protected internal override JsonObject Serialize()
    => new JsonObject()
    {
      ["type"] = "conundrum",
      ["state"] = State.ToString(),
      ["players"] = new JsonArray(Players.Select(x => JsonValue.Create(x)).ToArray()),
      ["message"] = Message?.Id ?? 0,
      ["letters"] = Letters,
      ["answer"] = Answer,
      ["submissions"] = new JsonArray(Submissions
        .Select(s => new JsonObject()
        {
          ["user"] = s.User,
          ["word"] = s.Word
        }).ToArray())
    };

  internal static void Deserialize(object _, DeserializeEventArgs<CountdownRound> args)
  {
    string type = (string)args.Object["type"];
    if (type != "conundrum") return;
    JsonObject obj = args.Object;

    args.Success = true;
    ConundrumRound round = new(args.Game, (ulong)obj["message"]);
    round.Players = ((JsonArray)obj["players"]).Select(x => (ulong)x).ToList();
    round.Submissions = ((JsonArray)obj["submissions"])
      .Select(n => (JsonObject)n)
      .Select(o => new ConundrumSubmission()
      {
        User = (ulong)o["user"],
        Word = (string)o["word"]
      }).ToList();
    round.Letters = (string)obj["letters"];
    round.Answer = (string)obj["answer"];

    args.Result = round;
  }

  public DiscordMessageBuilder GetMessageBuilder()
  {
    string roundHeader = $"# Round {Game.Rounds.Count} — Conundrum\n";
    string letters = (Letters != "") ? $"## Letters: {Letters}\n" : "Letters: Unknown\n";

    return new DiscordMessageBuilder().WithContent(roundHeader + letters);
  }

  protected internal async override ValueTask<bool> HandleDeparture(ulong player)
  {
    if (Submissions.Any(s => s.User == player)) return false;

    Players.Remove(player);
    if (CheckEndOfRound())
    {
      await HandleEndOfRound();
    }
    return true;
  }

  internal bool CheckEndOfRound()
  {
    return (!(Players.Except(Submissions.Select(s => s.User)).Any())
    && Answer != "");
  }

  internal bool CheckEndOfRound(ulong player)
  {
    return (!(Players.Except(Submissions.Select(s => s.User).Append(player)).Any())
    && Answer != "");
  }

  internal async Task HandleEndOfRound()
  {
    DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
      .WithTitle($"Round {Game.Rounds.Count} results:")
      .AddField("Letters:", Letters)
      .AddField("Answer:", Answer)
      .AddField("Submissions:", string
        .Join("\n", Submissions
          .Select((x, i) => $"{i + 1}. <@{x.User}>: {x.Word} {(x.Word == Answer ? "✅" : x.Word.IsValidGuess(Answer) ? "⚠️" : "❌")}")));

    if (Submissions.Any(x => x.Word == Answer))
      embed.AddField("Scores:", Submissions
        .Where(s => s.Word == Answer)
        .Select(s => $"- <@{s.User}>: 10 ⭐")
        .SJoin("\n"));
    else
      embed.AddField("Scores:", "None");

    await Game.Thread.SendMessageAsync(embed);

    SetState(GameState.RoundEnded);
    Message = null;
  }
}

internal struct ConundrumSubmission
{
  public ulong User;
  public string Word;
}

[Command("conundrum")]
[Description("Conundrum round commands")]
public class ConundrumCommands
{
  [Command("start")]
  [Description("Starts a conundrum round")]
  public async Task StartConundrumRound(SlashCommandContext ctx,
    [Description("Force the round to change, even if another round wasn't finished")] bool force = false
  )
  {
    if (!Condition.Check(ctx.Interaction,
      Condition.IsGameThread
        .And(Condition.IsGameHost)
        .And(Condition.NoRoundInProgress
        .AppendError(" Use `force:True` to bypass this.")),
    out string reason, out ConditionResult result) &&
    (result.FailedCondition != "NoRoundInProgress" || !force))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    ConundrumRound round = new(result.Game, null);

    var builder = round.GetMessageBuilder();
    await ctx.DeferResponseAsync();
    var message = await ctx.EditResponseAsync(builder);
    round.Message = message;
    round.MessageId = message.Id;
  }

  [Command("guess")]
  [Description("Submits a guess to a conundrum")]
  public async Task SubmitConundrumGuess(SlashCommandContext ctx,
    [Description("Your guess")] string guess
  )
  {
    if (!Condition.Check(ctx.Interaction,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsConundrumRound)
        .And(Condition.IsPlayerInRound)
        .And(Condition.NoPlayerSubmission),
    out string reason, out ConditionResult result
    ))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    ConundrumRound round = (ConundrumRound)result.Round;
    guess = guess.ToUpper().Trim();

    if (round.State == GameState.Declarations) round.SetState(GameState.DeclarationsMade);

    ulong userId = ctx.User.Id;
    bool roundEnding = round.CheckEndOfRound(userId);

    if (guess == "")
    {
      await ctx.RespondAsync("You didn't guess anything!");
      return;
    }

    round.Submissions.Add(new() { User = userId, Word = guess });

    await ctx.RespondAsync($"Your submission of `{guess}` has been recorded.", true);
    await ctx.FollowupAsync($"<@{userId}> has declared a guess!");

    if (roundEnding)
    {
      await round.HandleEndOfRound();
    }
  }

  [Command("pass")]
  [Description("Give up on the Conundrum")]
  public async Task PassConundrum(SlashCommandContext ctx)
  {
    if (!Condition.Check(ctx.Interaction,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsConundrumRound)
        .And(Condition.IsPlayerInRound)
        .And(Condition.NoPlayerSubmission),
    out string reason, out ConditionResult result
    ))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    ConundrumRound round = (ConundrumRound)result.Round;

    if (round.State == GameState.Declarations) round.SetState(GameState.DeclarationsMade);

    ulong userId = ctx.User.Id;
    bool roundEnding = round.CheckEndOfRound(userId);

    round.Submissions.Add(new() { User = userId, Word = "" });

    await ctx.RespondAsync($"Your pass has been recorded.");
    await ctx.FollowupAsync($"<@{userId}> has declared a guess!");

    if (roundEnding)
    {
      await round.HandleEndOfRound();
    }
  }

  [Command("answer")]
  [Description("Set the answer for a Conundrum round")]
  public async Task SetConundrumAnswer(SlashCommandContext ctx,
    [Description("Set the answer. If blank, sets a random conundrum.")] string answer = null,
    [Description("Set the displayed letters. If blank, the letters are randomized.")] string letters = null
  )
  {
    if (!Condition.Check(ctx.Interaction,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsConundrumRound)
        .And(Condition.IsGameHost)
        .And(Condition.ConundrumAnswerNotSet),
     out string reason, out ConditionResult result
    ))
    {
      await ctx.RespondAsync(reason);
      return;
    }

    ConundrumRound round = (ConundrumRound)result.Round;
    if (answer == null)
    {
      answer = ConundrumFunctions.Conundrums.OrderBy(x => Random.Shared.Next()).First();
    }
    else
    {
      answer = answer.Trim().ToUpper();
      if (!Regex.IsMatch(answer, "^[A-Z]*$"))
      {
        await ctx.RespondAsync($"`{answer}` isn't a valid answer!", true);
        return;
      }
    }

    if (letters == null)
    {
      letters = answer.OrderBy(x => Random.Shared.Next()).FormString();
    }
    else
    {
      letters = letters.Trim().ToUpper();
      if (letters.Order().FormString() != answer.Order().FormString())
      {
        await ctx.RespondAsync($"`{letters}` isn't an anagram of `{answer}`!", true);
        return;
      }
    }

    round.Answer = answer;
    round.Letters = letters;

    bool roundEnding = round.CheckEndOfRound();

    await ctx.RespondAsync("Updated!", true);
    await round.Message.ModifyAsync(round.GetMessageBuilder());

    if (roundEnding) await round.HandleEndOfRound();
  }

  [Command("close")]
  [Description("Close the round (cancel or end guessing)")]
  public async Task Close(SlashCommandContext ctx)
  {
    var intr = ctx.Interaction;

    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsRoundInProgress)
        .And(Condition.IsConundrumRound)
        .And(Condition.IsGameHost),
    out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    ConundrumRound round = (ConundrumRound)result.Round;

    if (round.Answer == "")
    {
      await ctx.RespondAsync("The active round has been cancelled!");
      round.SetState(GameState.RoundEnded);
    }
    else
    {
      await ctx.RespondAsync("The active round has been ended!");
      round.Players.RemoveAll(x => round.Submissions.Any(s => s.User == x));
      await round.HandleEndOfRound();
    }
  }
}

public static class ConundrumFunctions
{
  internal static AVLTreeSet<string> Conundrums;

  public static bool IsValidGuess(this string guess, string letters)
    => (Conundrums.Contains(guess.ToUpper()) || LetterFunctions.InWordList(guess)) && guess.Length == letters.Length
      && LetterFunctions.MissingLetters(letters, guess).Count() == 0;
}