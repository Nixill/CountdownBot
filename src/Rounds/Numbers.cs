using System.ComponentModel;
using System.Data;
using System.Text.Json.Nodes;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ArgumentModifiers;
using DSharpPlus.Commands.ContextChecks.ParameterChecks;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Nixill.CalcLib.Exception;
using Nixill.CalcLib.Objects;
using Nixill.CalcLib.Parsing;
using Nixill.Collections;
using Nixill.Utils;

namespace Nixill.Discord.Countdown;

public class NumbersRound : CountdownRound
{
  internal Dictionary<ulong, NumberSubmission> Submissions = new();
  internal List<int> Numbers { get; private set; } = new();
  internal int Target = 0;
  internal DiscordMessage Message;
  internal ulong MessageId;

  AVLTreeDictionary<int, List<Expression>> Solutions = null;
  Task NumberSolvingTask = null;

  internal bool IsSolvingNumbers => NumberSolvingTask?.Status == TaskStatus.Running;

  internal List<int> Larges = new() { 25, 50, 75, 100 };
  internal List<int> Smalls = new() { 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10 };
  internal int DrawCount = 6;

  int Closest = 0;

  internal List<string> Hints = new();

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

    args.Result = round;
  }

  public DiscordMessageBuilder GetMessageBuilder()
  {
    string roundHeader = $"# Round {Game.Rounds.Count} — Numbers:\n";
    string inControl = $"In control of the round: <@{Controller}>\n";
    string numbers = Numbers.Any() ? $"## Numbers: {string.Join(", ", Numbers)}\n" : "Numbers: Not yet chosen\n";
    string target = (Target != 0) ? $"## Target: {Target}\n" : "Target: Not yet chosen\n";

    DiscordMessageBuilder builder = new DiscordMessageBuilder()
      .WithContent(roundHeader + inControl + numbers + target);

    var range = Enumerable.Range(0, 1 + Math.Min(DrawCount, Larges.Count));

    if (State == GameState.Setup && Numbers.Count == 0)
    {
      builder.AddComponents(
        range
          .Take(5)
          .Select(x => new DiscordButtonComponent(DiscordButtonStyle.Primary, $"numbers-pick-{x}", x == 0 ? $"{DrawCount} Small" : $"{x} Large"))
      );
      if (range.Count() > 5)
        builder.AddComponents(
          range
            .Skip(5)
            .Select(x => new DiscordButtonComponent(DiscordButtonStyle.Primary, $"numbers-pick-{x}", $"{x} Large"))
        );
    }

    if (State == GameState.Setup && Numbers.Count == 6)
      builder.AddComponents(
          new DiscordButtonComponent(DiscordButtonStyle.Primary, "numbers-target", "Pick target")
        );

    return builder;
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

    if (submissionList.Any())
    {
      embed.AddField("Submissions", string.Join("\n", submissionList));
      embed.AddField("Judgments", string.Join("\n", judgmentList));
    }
    else
    {
      embed.AddField("Submissions", "There weren't any this round.");
    }

    if (!scores.Any(x => x.Score > 0))
      embed.AddField("Scores", "Nobody got any points this round.");
    else
    {
      var maxScores = scores.MaxManyBy(s => s.Priority);
      var zeroScores = scores.Where(s => s.Score == 0);
      var nonMaxScores = scores.Except(maxScores).Except(zeroScores).OrderByDescending(s => s.Priority);

      var maxScoreString = $"**Vs and solo scores:**\n{string.Join("\n", maxScores.Select(s => $"- <@{s.User}>: {s.Score}{(s.Priority == -minDifference ? " ⭐" : "")}"))}";
      var nonMaxScoreString = $"**Solo scores only:**\n{string.Join("\n", nonMaxScores.Select(s => $"- <@{s.User}>: {s.Score}"))}";
      var zeroScoreString = $"**Zero points, vs or solo:**\n{string.Join("\n", zeroScores.Select(s => $"- <@{s.User}>"))}";

      embed.AddField("Scores", maxScoreString + (nonMaxScores.Any() ? $"\n{nonMaxScoreString}" : "") + (zeroScores.Any() ? $"\n{zeroScoreString}" : ""));
    }

    await Game.Thread.SendMessageAsync(embed);

    SetState(GameState.RoundEnded);
    Message = null;
  }

  public void SetNumbers(IEnumerable<int> nums)
  {
    Numbers = new(nums);
    Solutions = null;
    NumberSolvingTask = Task.Run(SolveNumbers);
  }

  internal void SolveNumbers()
  {
    var exprs = NumberSolver.GetExpressionsFor(Numbers);
    Solutions = new AVLTreeDictionary<int, List<Expression>>(exprs
      .GroupBy(ex => ex.Value)
      .Select(g => new KeyValuePair<int, List<Expression>>(g.Key, g.ToList())));

    if (Target != 0) FindClosestSolutions();
  }

  internal void FindClosestSolutions()
  {
    // Gets the closest
    if (Solutions.ContainsKey(Target))
    {
      Closest = Target;
    }
    else
    {
      int closestAbove, closestBelow;

      if (!Solutions.TryGetHigherKey(Target, out closestAbove)) closestAbove = int.MaxValue;
      if (!Solutions.TryGetLowerKey(Target, out closestBelow)) closestBelow = int.MinValue;

      Closest = closestAbove;
      if (closestAbove - Target > Target - closestBelow) Closest = closestBelow;
    }

    List<Expression> exprList;

    if (Closest == Target)
    {
      Hints.Add($"There are {Solutions[Target].Count} solutions for {Target} with this selection.");
      Hints.Add($"The shortest solution uses {Solutions[Target].Min(l => l.BaseConstituents.Count())} given numbers.");
      exprList = Solutions[Target];
    }
    else
    {
      Hints.Add($"{Target} cannot be made with this selection.");
      Hints.Add($"The closest you can get is {Math.Abs(Closest - Target)} away.");
      exprList = Solutions[Closest];
    }

    var threeOrMore = exprList.Where(l => l.BaseConstituents.Count() >= 3);

    if (threeOrMore.Any())
    {
      CalcObject expr = ExpressionConverter.ToCalcLib(threeOrMore.MinBy(x => Random.Shared.Next()));
      CalcOperation oper = expr as CalcOperation;

      Hints.Add($"A randomly selected solution *ends* with {oper.Left.GetValue()} {oper.Operator.Symbol} {oper.Right.GetValue()}");
      string fifthHint = $"The selected solution is `{NumberFunctions.ExprToString(expr)}`.";

      while (oper.Left is CalcOperation || oper.Right is CalcOperation)
      {
        if (oper.Left is CalcOperation left) oper = left;
        else oper = (CalcOperation)oper.Right;
      }

      Hints.Add($"The selected solution *starts* with {oper.Left} {NumberFunctions.BetterSymbol(oper.Operator.Symbol)} {oper.Right}");
      Hints.Add(fifthHint);
    }
    else
    {
      CalcObject expr = ExpressionConverter.ToCalcLib(exprList.MinBy(x => Random.Shared.Next()));

      Hints.Add($"No solution exists that uses three or more numbers.");

      if (expr is CalcOperation oper)
      {
        Hints.Add($"A solution with two numbers uses the {oper.Operator} operator.");
      }
      else
      {
        Hints.Add($"There isn't even a two-number solution, it's only a given number.");
      }

      Hints.Add($"The selected solution is {NumberFunctions.ExprToString(expr)}");
    }
  }

  public async void WaitForNumbersSolved()
    => await NumberSolvingTask;
}

public static class NumberInteractionLogic
{
  public static async Task PickNums(DiscordInteraction intr, int numLarges)
  {
    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsNumbersRound)
        .And(Condition.AreNumbersDrawing)
        .And(Condition.IsRoundControllerOrHost),
    out string reason, out ConditionResult result))
    {
      await intr.EphemeralResponse(reason);
      return;
    }

    CountdownGame game = result.Game;
    NumbersRound round = (NumbersRound)result.Round;

    if (intr.Type == DiscordInteractionType.Component)
      await intr.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage);

    round.SetNumbers(round.Larges
      .OrderBy(x => Random.Shared.Next())
      .Take(Math.Min(Math.Min(numLarges, round.DrawCount), round.Larges.Count))
      .Concat(round.Smalls
        .OrderBy(x => Random.Shared.Next())
        .Take(round.DrawCount - Math.Min(Math.Min(numLarges, round.DrawCount), round.Larges.Count))));

    await round.Message.ModifyAsync(round.GetMessageBuilder());

    if (intr.Type == DiscordInteractionType.ApplicationCommand)
      await intr.EphemeralResponse("Drawn!");

    if (round.Target != 0)
    {
      round.SetState(GameState.Declarations);
      await intr.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder()
        .WithContent("Numbers and target are chosen! Submit your declarations now using `/numbers declare`.\n"
          + $"<@{round.Controller}>, you should declare first!"));
    }
  }

  public static async Task PickTarget(DiscordInteraction intr, int lowerBound = 100, int higherBound = 999)
  {
    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsNumbersRound)
        .And(Condition.IsTargetDrawing)
        .And(Condition.IsRoundControllerOrHost)
        .And(Condition.IsGameHost
          .AppendError(" The controller may only pick a random target 100-999, not a specific target.")),
    out string reason, out ConditionResult result)
      && !(result.FailedCondition == "IsGameHost" && lowerBound == 100 && higherBound == 999)
    )
    {
      await intr.EphemeralResponse(reason);
      return;
    }

    CountdownGame game = result.Game;
    NumbersRound round = (NumbersRound)result.Round;

    if (intr.Type == DiscordInteractionType.Component)
      await intr.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage);

    round.Target = (int)Random.Shared.NextInt64(lowerBound, higherBound + 1);

    await round.Message.ModifyAsync(round.GetMessageBuilder());

    if (intr.Type == DiscordInteractionType.ApplicationCommand)
      await intr.EphemeralResponse("Picked!");

    if (round.Numbers.Any())
    {
      round.SetState(GameState.Declarations);
      await intr.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder()
        .WithContent("Numbers and target are chosen! Submit your declarations now using `/numbers declare`.\n"
          + $"<@{round.Controller}>, you should declare first!"));

      if (!round.IsSolvingNumbers) round.FindClosestSolutions();
    }
  }
}

[Command("numbers")]
[Description("Numbers round commands")]
public class NumbersCommands
{
  [Command("start")]
  [Description("Starts a numbers round")]
  public async Task StartNumbersRound(SlashCommandContext ctx,
    [Description("The controller of this round")] DiscordUser controller = null,
    [Description("Force the round to change, even if another round wasn't finished")] bool force = false,
    [Description("Pre-populate the numbers selection.")] string numbers = null,
    [Description("Pre-select a target.")][MinMaxValue(minValue: 1)] int? target = null,
    [Description("Change the large numbers deck.")] string largeDeck = null,
    [Description("Change the small numbers deck.")] string smallDeck = null,
    [Description("Change the draw count")][MinMaxValue(minValue: 2, maxValue: 8)] int drawCount = 6
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

    ulong contID = controller?.Id ?? 0;
    await ctx.DeferResponseAsync();
    string error = "Errors:\n";
    bool anyError = false;

    NumbersRound round = new(result.Game, null, contID);

    if (numbers != null)
    {
      var nums = numbers.Split(" ").Select(IntParseResult.Try);

      round.SetNumbers(nums
        .Where(p => p.Success)
        .Take(8)
        .Select(p => p.Value));

      if (nums.Any(p => !p.Success))
      {
        anyError = true;
        error += $"- The following number(s) couldn't be parsed and were ignored: `{string.Join("`, `", nums
          .Where(p => !p.Success).Select(p => p.Input))}`.\n";
      }

      if (nums.Where(p => p.Success).Skip(8).Any())
      {
        anyError = true;
        error += $"- Only 8 numbers may be specified in a round, due to constraints on the solver." +
        $" The following extra number(s) were ignored: `{string.Join("`, `", nums
          .Where(p => p.Success).Skip(8).Select(p => p.Value))}`.";
      }
    }

    if (target.HasValue)
    {
      round.Target = target.Value;
    }

    round.DrawCount = drawCount;

    if (smallDeck != null)
    {
      var nums = smallDeck.Split(" ").Select(IntParseResult.Try);

      var successful = nums.Where(r => r.Success).Select(r => r.Value).ToList();
      var failed = nums.Where(r => !r.Success).Select(r => r.Input);

      if (failed.Any())
      {
        anyError = true;
        error += $"- For the small deck, the following numbers couldn't be parsed and were ignored: `{string.Join("`, `", failed)}`.";
      }

      if (successful.Count == 0)
      {
        anyError = true;
        error += $"- For the small deck, the input `{smallDeck}` contained no valid numbers.";
      }
      else if (successful.Count < drawCount)
      {
        anyError = true;
        error += $"- For the small deck, the input `{smallDeck}` did not contain enough numbers to form a deck.";
      }
      else
      {
        round.Smalls = successful;
      }
    }

    if (largeDeck != null)
    {
      var nums = largeDeck.Split(" ").Select(IntParseResult.Try);

      var successful = nums.Where(r => r.Success).Select(r => r.Value).ToList();
      var failed = nums.Where(r => !r.Success).Select(r => r.Input);

      if (failed.Any())
      {
        anyError = true;
        error += $"- For the large deck, the following numbers couldn't be parsed and were ignored: `{string.Join("`, `", failed)}`.";
      }

      if (successful.Count == 0)
      {
        anyError = true;
        error += $"- For the large deck, the input `{largeDeck}` contained no valid numbers.";
      }
      else
      {
        round.Larges = successful;
      }
    }

    if (round.Numbers.Any() && target != 0)
    {
      round.SetState(GameState.Declarations);
    }

    var builder = round.GetMessageBuilder();
    var message = await ctx.EditResponseAsync(builder);
    round.Message = message;
    round.MessageId = message.Id;

    if (round.Numbers.Any() && target != 0)
    {
      await ctx.FollowupAsync("The numbers and target are chosen! Submit your declarations now using `/numbers declare`.\n"
          + $"<@{round.Controller}>, you should declare first!");
    }

    if (anyError) await ctx.FollowupAsync(error, true);
  }

  [Command("draw")]
  [Description("Selects numbers for the numbers round.")]
  public async Task DrawNumbers(SlashCommandContext ctx,
    [Description("How many large numbers to draw")][MinMaxValue(minValue: 0, maxValue: 8)] int larges
  ) => await NumberInteractionLogic.PickNums(ctx.Interaction, larges);

  [Command("pick")]
  [Description("Picks specific numbers for the numbers round.")]
  public async Task PickNumbers(SlashCommandContext ctx,
    [Description("Which numbers to pick")] string numbers
  )
  {
    DiscordInteraction intr = ctx.Interaction;

    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsNumbersRound)
        .And(Condition.AreNumbersDrawing)
        .And(Condition.IsRoundControllerOrHost
          .WithError("You are not the game's host!"))
        .And(Condition.IsGameHost
          .AppendError(" The controller may only draw letters, not force specifics.")),
      out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    NumbersRound round = (NumbersRound)result.Round;

    var nums = numbers.Split(" ").Select(IntParseResult.Try);
    var success = nums.Where(p => p.Success).Select(p => p.Value);
    var fail = nums.Where(p => !p.Success).Select(p => p.Input);

    if (success.Count() > 2)
    {
      await ctx.RespondAsync("Picked!", true);

      round.SetNumbers(success.Take(8));
      round.SetState(GameState.Declarations);

      if (fail.Any())
      {
        await ctx.FollowupAsync($"The following number(s) couldn't be parsed and were ignored: `{string.Join("`, `", nums
          .Where(p => !p.Success).Select(p => p.Input))}`.\n", true);
      }

      if (success.Skip(8).Any())
      {
        await ctx.FollowupAsync($"Only 8 numbers may be specified in a round, due to constraints on the solver." +
        $" The following extra number(s) were ignored: `{string.Join("`, `", nums
          .Where(p => p.Success).Skip(8).Select(p => p.Value))}`.", true);
      }
    }
    else if (success.Count() == 1)
    {
      await ctx.RespondAsync("At least two numbers must be drawn.", true);
    }
    else
    {
      await ctx.RespondAsync($"The input `{numbers}` could not be parsed into any numbers.");
    }

    await round.Message.ModifyAsync(round.GetMessageBuilder());

    if (round.Target != 0)
    {
      round.SetState(GameState.Declarations);
      await intr.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder()
        .WithContent("Numbers and target are chosen! Submit your declarations now using `/numbers declare`.\n"
          + $"<@{round.Controller}>, you should declare first!"));
    }
  }

  [Command("target")]
  [Description("Select the target for the round")]
  public async Task SetTarget(SlashCommandContext ctx,
    [Description("The lower bound of the random target (default 100).")] int lowerBound = 100,
    [Description("The upper bound of the random target (default 999).")] int upperBound = 999)
  => await NumberInteractionLogic.PickTarget(ctx.Interaction, lowerBound, upperBound);

  [Command("declare")]
  [Description("Submits a declaration for the given numbers")]
  public async Task DeclareEquation(SlashCommandContext ctx,
    [Description("The result of the equation being declared")] int value,
    [Description("The expression being declared")] string expression
  )
  {
    DiscordInteraction intr = ctx.Interaction;

    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsNumbersRound)
        .And(Condition.IsPlayerInRound)
        .And(Condition.CanRoundDeclare),
      out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    NumbersRound round = (NumbersRound)result.Round;

    if (round.State == GameState.Declarations) round.SetState(GameState.DeclarationsMade);

    ulong userId = ctx.User.Id;

    if (round.Submissions.ContainsKey(userId))
    {
      await ctx.RespondAsync("You've already submitted an equation! You can use `/numbers extra` if you've found a better solution (but it won't score).", true);
      return;
    }

    // Sanitize inputs
    try
    {
      expression = expression.Select(c => c switch
      {
        '[' or '{' or '<' => '(',
        ']' or '}' or '>' => ')',
        '×' or 'x' or 'X' => '*',
        ':' or '÷' => '/',
        _ => c
      }).Where(c => c switch
      {
        (>= '0' and <= '9') or '+' or '-' or '*' or '/' or '(' or ')' or '=' => true,
        ' ' or '|' or '\t' or '\n' or '`' => false,
        _ => throw new InvalidOperationException()
      }).FormString().Split("=").MaxBy(s => s.Length);
    }
    catch (InvalidOperationException)
    {
      await ctx.RespondAsync("Only the characters `0123456789+-*/()[]{}×Xx:÷`, and some whitespace (which is dropped), are allowed in an equation.", true);
      return;
    }

    // Create expression
    CalcObject expr = null;

    try
    {
      expr = CLInterpreter.Interpret(expression);
    }
    catch (CLSyntaxException e)
    {
      await ctx.RespondAsync($"Your input wasn't a valid equation:\n```\n{expression}\n{new string(' ', e.Position)}↑\n```\n{e.Message}", true);
      return;
    }

    NumberSubmission submission = new(expr, value, round.Numbers);
    round.Submissions[userId] = submission;

    await ctx.RespondAsync($"Your submission of `{submission.DeclaredAs} ={(submission.DeclaredAs == round.Target ? "=" : "")} {submission.Expression}` has been recorded.", true);
    await ctx.FollowupAsync($"<@{userId}> has declared reaching {value}.");

    if (!(round.Players.Except(round.Submissions.Keys).Any()))
    {
      await round.HandleEndOfRound();
    }
  }

  [Command("pass")]
  [Description("Leave the round with no guess")]
  public async Task Pass(SlashCommandContext ctx)
  {
    var intr = ctx.Interaction;

    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsNumbersRound)
        .And(Condition.IsPlayerInRound)
        .And(Condition.CanRoundDeclare),
      out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    await ctx.RespondAsync($"<@{ctx.User.Id}> has passed.");

    NumbersRound round = (NumbersRound)result.Round;

    await round.HandleDeparture(ctx.User.Id);
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
        .And(Condition.IsNumbersRound)
        .And(Condition.IsGameHost),
    out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    NumbersRound round = (NumbersRound)result.Round;

    if (round.State == GameState.Setup || round.State == GameState.Declarations)
    {
      await ctx.RespondAsync("The active round has been cancelled!");
      round.SetState(GameState.RoundEnded);
    }
    else
    {
      await ctx.RespondAsync("The active round has been ended!");
      round.Players.RemoveAll(x => !round.Submissions.ContainsKey(x));
      await round.HandleEndOfRound();
    }
  }

  [Command("extra")]
  [Description("Submit extra guesses (for bragging rights) after your main guess")]
  public async Task Extra(SlashCommandContext ctx,
    [Description("The value to declare as")] int value,
    [Description("The expression to submit")] string expression
  )
  {
    if (!Condition.Check(ctx.Interaction,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsNumbersRound)
        .And(Condition.NotAwaitingNumbersSubmission),
    out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    NumbersRound round = (NumbersRound)result.Round;

    // Sanitize inputs
    try
    {
      expression = expression.Select(c => c switch
      {
        '[' or '{' or '<' => '(',
        ']' or '}' or '>' => ')',
        '×' or 'x' or 'X' => '*',
        ':' or '÷' => '/',
        _ => c
      }).Where(c => c switch
      {
        (>= '0' and <= '9') or '+' or '-' or '*' or '/' or '(' or ')' or '=' => true,
        ' ' or '|' or '\t' or '\n' or '`' => false,
        _ => throw new InvalidOperationException()
      }).FormString().Split("=").MaxBy(s => s.Length);
    }
    catch (InvalidOperationException)
    {
      await ctx.RespondAsync("Only the characters `0123456789+-*/()[]{}×Xx:÷`, and some whitespace (which is dropped), are allowed in an equation.", true);
      return;
    }

    // Create expression
    CalcObject expr = null;

    try
    {
      expr = CLInterpreter.Interpret(expression);
    }
    catch (CLSyntaxException e)
    {
      await ctx.RespondAsync($"Your input wasn't a valid equation:\n```\n{expression}\n{new string(' ', e.Position)}↑\n```\n{e.Message}", true);
      return;
    }

    NumberSubmission submission = new(expr, value, round.Numbers);

    if (submission.Valid)
    {
      await ctx.RespondAsync($"<@{ctx.User.Id}> has found {submission.Value} ={(submission.Value == round.Target ? "=" : "")} ||{submission.Expression}||");
      return;
    }

    string error = "That's not a valid submission:\n";

    if (submission.DeclaredAs != submission.Value) error += $"- It actually computes as {submission.Value}\n";
    var exceptions = submission.BaseNumbers.ExceptInstances(round.Numbers);
    if (exceptions.Any()) error += $"- It uses numbers that weren't given: {string.Join(", ", exceptions)}\n";
    if (submission.Lowest == 0) error += "- It creates a zero\n";
    if (submission.Lowest < 0) error += "- It creates a negative number\n";
    if (submission.NonInt) error += "- It creates a non-integer\n";

    await ctx.RespondAsync(error, true);
    return;
  }

  [Command("hint")]
  [Description("Get a hint about this numbers round")]
  public async Task Hint(SlashCommandContext ctx)
  {
    if (!Condition.Check(ctx.Interaction,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsNumbersRound)
        .And(Condition.NotAwaitingNumbersSubmission),
    out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    NumbersRound round = (NumbersRound)result.Round;

    if (round.IsSolvingNumbers)
    {
      await ctx.RespondAsync("Cannot hint yet: Numbers are still being solved!");
      return;
    }

    if (round.Hints.Count == 0)
    {
      await ctx.RespondAsync("Cannot hint: All hints have been given already!");
      return;
    }

    string hint = round.Hints.Pop();
    await ctx.RespondAsync($"Hint: ||{hint}||");
  }
}

public static class NumberButtonEvents
{
  public static async Task PickNumbersButtonPressed(DiscordClient discord, ComponentInteractionCreateEventArgs args)
  {
    if (!args.Id.StartsWith("numbers-pick-")) return;
    await NumberInteractionLogic.PickNums(args.Interaction, int.Parse(args.Id[13..]));
  }

  public static async Task PickTargetButtonPressed(DiscordClient discord, ComponentInteractionCreateEventArgs args)
  {
    if (args.Id != "numbers-target") return;
    await NumberInteractionLogic.PickTarget(args.Interaction);
  }
}

public static class NumberFunctions
{
  public static string ExprToString(CalcObject obj)
  {
    if (obj is CalcOperation oper)
    {
      var left = oper.Left;
      var right = oper.Right;
      var lString = ExprToString(left);
      var rString = ExprToString(right);

      if (left is CalcOperation lOper && oper.Operator.Priority > lOper.Operator.Priority) lString = $"({lString})";
      if (right is CalcOperation rOper && (oper.Operator.Priority > rOper.Operator.Priority || (oper.Operator.Symbol == "-" && rOper.Operator.Priority == oper.Operator.Priority) || oper.Operator.Symbol == "/")) rString = $"({rString})";
      return $"{lString} {BetterSymbol(oper.Operator.Symbol)} {rString}";
    }

    else return obj.ToString();
  }

  public static string BetterSymbol(string symbol) => symbol switch
  {
    "*" => "×",
    "/" => "÷",
    _ => symbol
  };

  public static IEnumerable<int> GetBaseNumbers(CalcObject obj)
  {
    if (obj is CalcOperation oper)
    {
      foreach (int i in GetBaseNumbers(oper.Left)) yield return i;
      foreach (int i in GetBaseNumbers(oper.Right)) yield return i;
    }

    else if (obj is CalcNumber num)
    {
      yield return (int)num.Value;
    }

    else yield break;
  }

  public static IEnumerable<decimal> GetAllValues(CalcObject obj)
  {
    if (obj is CalcOperation oper)
    {
      foreach (decimal d in GetAllValues(oper.Left)) yield return d;
      foreach (decimal d in GetAllValues(oper.Right)) yield return d;
    }

    CalcNumber num = obj.GetValue() as CalcNumber;
    if (num != null) yield return num.Value;
  }

  public static int GetValue(CalcObject obj)
  {
    CalcNumber num = obj.GetValue() as CalcNumber;
    return (int)(num?.Value ?? 0);
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

  public NumberSubmission() { }

  public NumberSubmission(CalcObject obj, int declaredAs, IEnumerable<int> givenNumbers)
  {
    DeclaredAs = declaredAs;
    Expression = NumberFunctions.ExprToString(obj);
    Value = NumberFunctions.GetValue(obj);
    BaseNumbers = NumberFunctions.GetBaseNumbers(obj).ToArray();
    Lowest = (int)NumberFunctions.GetAllValues(obj).Min();
    NonInt = NumberFunctions.GetAllValues(obj).Any(d => d.Scale > 0);
    Valid = (
      DeclaredAs == Value &&
      (!BaseNumbers.ExceptInstances(givenNumbers).Any()) &&
      Lowest > 0 &&
      !NonInt
    );
  }
}

public struct IntParseResult
{
  public bool Success;
  public int Value;
  public string Input;

  public IntParseResult(bool success, int value, string input)
  {
    Success = success;
    Value = value;
    Input = input;
  }

  public IntParseResult(string input)
  {
    Success = int.TryParse(input, out Value);
    Input = input;
  }

  public static IntParseResult Try(string input) => new(input);
}