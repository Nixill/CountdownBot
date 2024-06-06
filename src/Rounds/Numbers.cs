using System.Data;
using System.Text.Json.Nodes;
using DSharpPlus.Entities;
using Nixill.CalcLib.Objects;
using Nixill.Collections;
using Nixill.Utils;

namespace Nixill.Discord.Countdown;

public class NumbersRound : CountdownRound
{
  internal Dictionary<ulong, NumberSubmission> Submissions = new();
  internal List<int> Numbers = new();
  internal int Target = 0;
  internal DiscordMessage Message;
  internal ulong MessageId;

  internal AVLTreeDictionary<int, IEnumerable<Expression>> Expressions;
  int? ClosestBelow;
  int? ClosestAbove;
  int Closest = 0;

  public NumbersRound(CountdownGame game, DiscordMessage message, ulong controller = 0) : base(game, controller)
  {
    Message = message;
    MessageId = message?.Id ?? 0;
  }

  public NumbersRound(CountdownGame game, ulong messageId, ulong controller = 0) : base(game, controller)
  {
    MessageId = messageId;
    if (messageId != 0) Task.Run(async () => Message = await (await CountdownBotMain.Discord.GetChannelAsync(Game.ThreadId)).GetMessageAsync(MessageId));
  }

  public override IEnumerable<(ulong Player, int Score, int Priority)> Scores =>
    Submissions.Select(x =>
      x.Value.Valid ?
        (x.Key, (x.Value.Value - Target) switch
        {
          0 => 10,
          >= -5 and <= 5 => 7,
          >= -10 and <= 10 => 5,
          _ => 0
        }, -Math.Abs(x.Value.Value - Target)) : (x.Key, 0, int.MinValue));

  protected internal override JsonObject Serialize()
  {
    JsonObject roundObject = new();
    roundObject["type"] = "numbers";
    roundObject["controller"] = Controller;
    roundObject["state"] = State.ToString();
    roundObject["players"] = new JsonArray(Players.Select(x => JsonValue.Create(x)).ToArray());
    roundObject["message"] = Message?.Id ?? 0;
    roundObject["numbers"] = new JsonArray(Numbers.Select(x => JsonValue.Create(x)).ToArray());
    roundObject["target"] = Target;
    JsonObject submissionsObject = new();
    Submissions.Select(kvp => new KeyValuePair<string, JsonNode>(kvp.Key.ToString(), new JsonObject()
    {
      ["expression"] = kvp.Value.Expression,
      ["value"] = kvp.Value.Value,
      ["valid"] = kvp.Value.Valid,
      ["declared"] = kvp.Value.DeclaredAs
    })).Do(submissionsObject.Add);
    roundObject["submissions"] = submissionsObject;

    return roundObject;
  }

  internal static void Deserialize(object _, DeserializeEventArgs<CountdownRound> args)
  {
    string type = (string)args.Object["type"];
    if (type != "numbers") return;
    JsonObject obj = args.Object;

    args.Success = true;
    NumbersRound round = new(args.Game, (ulong)obj["message"], (ulong)obj["controller"]);
    round.Players = ((JsonArray)obj["players"]).Select(x => (ulong)x).ToList();
    round.Submissions = ((JsonObject)obj["submissions"])
      .Select(kvp => new KeyValuePair<ulong, NumberSubmission>(ulong.Parse(kvp.Key), new NumberSubmission()
      {
        Valid = (bool)kvp.Value["valid"],
        Expression = (string)kvp.Value["expression"],
        Value = (int)kvp.Value["value"],
        DeclaredAs = (int)kvp.Value["declared"]
      })).ToDictionary();
    round.Numbers = ((JsonArray)obj["numbers"]).Select(x => (int)x).ToList();
    round.Target = (int)obj["target"];
    if (!Enum.TryParse<GameState>((string)obj["state"], out GameState state)) state = GameState.Setup;
    round.SetState(state);
  }

  public DiscordMessageBuilder GetMessageBuilder()
  {
    string roundHeader = $"# Round {Game.Rounds.Count} — Numbers:\n";
    string inControl = $"In control of the round: <@{Controller}>\n";

    if (State == GameState.Setup && Numbers.Count == 0)
      return new DiscordMessageBuilder().WithContent(roundHeader + inControl + "Use a button below to select numbers.")
        .AddComponents(
          new DiscordButtonComponent(DiscordButtonStyle.Primary, "numbers-pick-0", "6 Small"),
          new DiscordButtonComponent(DiscordButtonStyle.Primary, "numbers-pick-1", "1 Large"),
          new DiscordButtonComponent(DiscordButtonStyle.Primary, "numbers-pick-2", "2 Large"),
          new DiscordButtonComponent(DiscordButtonStyle.Primary, "numbers-pick-3", "3 Large"),
          new DiscordButtonComponent(DiscordButtonStyle.Primary, "numbers-pick-4", "4 Large")
        );
    else if (State == GameState.Setup && Numbers.Count == 6)
      return new DiscordMessageBuilder().WithContent(roundHeader + inControl + $"## Numbers: {string.Join(" ", Numbers)}\n"
        + "Click below to pick the target.")
        .AddComponents(
          new DiscordButtonComponent(DiscordButtonStyle.Primary, "numbers-target", "Pick target")
        );
    else return new DiscordMessageBuilder().WithContent(roundHeader + inControl + $"## Numbers: {string.Join(" ", Numbers)} → Target: {Target}");
  }

  protected internal async override ValueTask<bool> HandleDeparture(ulong player)
  {
    if (Submissions.ContainsKey(player)) return false;

    Players.Remove(player);
    if (!(Players.Except(Submissions.Keys).Any()))
    {
      await HandleEndOfRound();
    }
    return true;
  }

  internal async Task HandleEndOfRound()
  {
    DiscordEmbedBuilder embed = new DiscordEmbedBuilder()
      .WithTitle($"Round {Game.Rounds.Count} results:")
      .AddField("Given numbers:", $"{string.Join(" ", Numbers)} → {Target}");

    List<string> submissionList = new();
    List<string> judgmentList = new();
    List<(ulong User, int Score, int Priority)> scores = new();

    int minDifference = Math.Abs(Closest - Target);

    foreach (var submission in Submissions)
    {
      submissionList.Add($"- <@{submission.Key}>: {submission.Value.DeclaredAs} ={((submission.Value.DeclaredAs == Target) ? "=" : "")} ||{submission.Value.Expression}||");

      if (submission.Value.Valid)
      {
        int difference = Math.Abs(submission.Value.Value - Target);

        if (difference > 10)
        {
          judgmentList.Add($"- <@{submission.Key}>, while your equation is accurate, it's more than ten away from the target.");
          scores.Add((submission.Key, difference switch { 0 => 10, <= 5 => 7, <= 10 => 5, _ => 0 }, -difference));
        }
        else if (difference == 0)
        {
          judgmentList.Add($"- <@{submission.Key}>, on the nose! 10 points to you.");
          scores.Add((submission.Key, 10, 0));
        }
        else
        {
          judgmentList.Add($"- <@{submission.Key}>, your equation is accurate.");
          scores.Add((submission.Key, difference switch { 0 => 10, <= 5 => 7, <= 10 => 5, _ => 0 }, -difference));
        }
      }
      else
      {
        scores.Add((submission.Key, 0, int.MinValue));

        IEnumerable<int> numbersNotGiven;

        if (submission.Value.Value != submission.Value.DeclaredAs)
          judgmentList.Add($"- <@{submission.Key}>, your equation does not equal {submission.Value.DeclaredAs}, ||but rather {submission.Value.Value}||.");
        else if ((numbersNotGiven = submission.Value.BaseNumbers.ExceptInstances(Numbers)).Count() == 1)
          judgmentList.Add($"- <@{submission.Key}>, your equation uses a number that wasn't given: ||{numbersNotGiven.First()}||");
        else if (numbersNotGiven.Count() > 1)
          judgmentList.Add($"- <@{submission.Key}>, your equation uses numbers that weren't given: ||{string.Join(", ", numbersNotGiven)}");
        else if (submission.Value.Lowest == 0)
          judgmentList.Add($"- <@{submission.Key}>, your equation produced a zero.");
        else if (submission.Value.Lowest < 0)
          judgmentList.Add($"- <@{submission.Key}>, your equation produced a negative number.");
        else if (submission.Value.NonInt)
          judgmentList.Add($"- <@{submission.Key}>, your equation produced a non-integer.");
      }
    }

    embed.AddField("Submissions", string.Join("\n", submissionList));
    embed.AddField("Judgments", string.Join("\n", judgmentList));

    if (!scores.Any(x => x.Score > 0))
      embed.AddField("Scores", "Nobody got any points this round.");
    else
    {
      var maxScores = scores.MaxManyBy(s => s.Priority);
      var nonMaxScores = scores.Except(maxScores).OrderByDescending(s => s.Priority);

      var maxScoreString = $"### Vs and solo scores:\n{string.Join("\n", maxScores.Select(s => $"- <@{s.User}>: {s.Score}{(s.Priority == -minDifference ? " ⭐" : "")}"))}";
      var nonMaxScoreString = $"### Solo scores only:\n{string.Join("\n", nonMaxScores.Select(s => $"- <@{s.User}>: {s.Score}"))}";

      embed.AddField("Scores", maxScoreString + (nonMaxScores.Any() ? $"\n{nonMaxScoreString}" : ""));
    }

    await Game.Thread.SendMessageAsync(embed);

    SetState(GameState.RoundEnded);
    Message = null;
  }
}

public static class NumberInteractionLogic
{
  public static async Task PickNums(DiscordInteraction intr, int numLarges)
  {
    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsNumbersRound)
        .And(Condition.AreNumbersDrawing),
    out string reason, out ConditionResult result))
    {
      await intr.EphemeralResponse(reason);
      return;
    }
  }
}

public struct NumberSubmission
{
  public string Expression;
  public int Value;
  public bool Valid;
  public int DeclaredAs;
  public int[] BaseNumbers;
  public int Lowest;
  public bool NonInt;
}