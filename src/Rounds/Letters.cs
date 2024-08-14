using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Nixill.Collections;
using Nixill.Utils;

namespace Nixill.Discord.Countdown;

public static class LetterDecks
{
  public const string Consonants = "BBCCCDDDDDDFFGGGGHHJKLLLLLMMMMNNNNNNNNPPPPQRRRRRRRRRSSSSSSSSSTTTTTTTTTVVWWXYZ";
  public const string Vowels = "AAAAAAAAAAAAAAAEEEEEEEEEEEEEEEEEEEEEIIIIIIIIIIIIIOOOOOOOOOOOOOUUUUU";

  public static Dictionary<ulong, (string Consonants, string Vowels)> Decks = new();

  static Random Randomizer = new();

  public static char Draw(ulong threadID, bool vowel)
  {
    if (!Decks.ContainsKey(threadID)) Decks[threadID] = (Consonants, Vowels);
    var decks = Decks[threadID];

    var deck = (vowel) ? decks.Vowels : decks.Consonants;

    if (deck == "") deck = (vowel) ? Vowels : Consonants;

    int index = (int)Randomizer.NextInt64(deck.Length);
    char ret = deck[index];

    var newDeck = ((index > 0) ? deck[0..index] : "")
      + ((index < deck.Length) ? deck[(index + 1)..^0] : "");

    Decks[threadID] = (vowel) ? (decks.Consonants, newDeck) : (newDeck, decks.Vowels);

    return ret;
  }

  public static void Reset(ulong threadID)
  {
    Decks[threadID] = (Consonants, Vowels);
  }
}

public class LettersRound : CountdownRound
{
  internal Dictionary<ulong, string> Submissions = new();
  internal Dictionary<ulong, (string, int)> InvalidSubmissions = new();
  internal List<char> Letters = new();
  internal DiscordMessage Message;
  internal ulong MessageId;

  public int Vowels => Letters.Where(c => c switch { 'A' or 'E' or 'I' or 'O' or 'U' => true, _ => false }).Count();
  public int Consonants => Letters.Where(c => c switch { 'A' or 'E' or 'I' or 'O' or 'U' => false, _ => true }).Count();

  public LettersRound(CountdownGame game, DiscordMessage message, ulong controller = 0) : base(game, controller)
  {
    Message = message;
    MessageId = message?.Id ?? 0;
  }

  public LettersRound(CountdownGame game, ulong messageId, ulong controller = 0) : base(game, controller)
  {
    MessageId = messageId;
    if (messageId != 0) Task.Run(async () => Message = await (await CountdownBotMain.Discord.GetChannelAsync(Game.ThreadId)).GetMessageAsync(MessageId));
  }

  public override IEnumerable<(ulong Player, int Score, int Priority)> Scores =>
    Submissions.Select(x =>
      LetterFunctions.InWordList(x.Value)
        ? (x.Key, (x.Value.Length == 9 ? 18 : x.Value.Length), x.Value.Length)
        : (x.Key, 0, 0));

  protected internal override JsonObject Serialize()
  {
    JsonObject roundObject = new();
    roundObject["type"] = "letters";
    roundObject["controller"] = Controller;
    roundObject["state"] = State.ToString();
    roundObject["letters"] = Letters.FormString();
    roundObject["players"] = new JsonArray(Players.Select(x => JsonValue.Create(x)).ToArray());
    roundObject["message"] = Message?.Id ?? 0;
    JsonObject submissionsObject = new();
    Submissions.Select(kvp => new KeyValuePair<string, JsonNode>(kvp.Key.ToString(), JsonValue.Create(kvp.Value))).Do(submissionsObject.Add);
    roundObject["submissions"] = submissionsObject;

    return roundObject;
  }

  internal static void Deserialize(object _, DeserializeEventArgs<CountdownRound> args)
  {
    string type = (string)args.Object["type"];
    if (type != "letters") return;
    JsonObject obj = args.Object;

    args.Success = true;
    LettersRound round = new(args.Game, (ulong)obj["message"], (ulong)obj["controller"]);
    round.Players = ((JsonArray)obj["players"]).Select(x => (ulong)x).ToList();
    round.Submissions = ((JsonObject)obj["submissions"]).Select(kvp => new KeyValuePair<ulong, string>(ulong.Parse(kvp.Key), (string)kvp.Value)).ToDictionary();
    round.Letters = ((string)obj["letters"]).ToList();
    if (!Enum.TryParse<GameState>((string)obj["state"], out GameState state)) state = GameState.Setup;
    round.SetState(state);
  }

  public DiscordMessageBuilder GetMessageBuilder()
    => new DiscordMessageBuilder()
      .WithContent($"# Round {Game.Rounds.Count} — Letters:\n"
        + $"## Current letters selection: {((Letters.Count > 0) ? $"`{Letters.FormString()}`" : "(None)")}\n"
        + $"In control of the round: <@{Controller}>\n")
      .AddComponents(
        new DiscordButtonComponent(
          DiscordButtonStyle.Primary,
          "letters-draw-consonant",
          "+ Consonant",
          !(State == GameState.Setup && Consonants < 6)
        ),
        new DiscordButtonComponent(
          DiscordButtonStyle.Primary,
          "letters-draw-vowel",
          "+ Vowel",
          !(State == GameState.Setup && Vowels < 5)
        )
      );

  public void SetMessage(DiscordMessage msg)
  {
    Message = msg;
    MessageId = msg.Id;
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
    string givenLetters = string.Join(" ", Letters);

    var submissionsAsDeclared = new Dictionary<ulong, (string Word, int Length)>(InvalidSubmissions);
    Submissions.Where(x => !InvalidSubmissions.ContainsKey(x.Key))
      .Select(x => new KeyValuePair<ulong, (string, int)>(x.Key, (x.Value, x.Value.Length)))
      .Do(x => submissionsAsDeclared.Add(x.Key, x.Value));

    string wordsSubmitted = string.Join("\n", submissionsAsDeclared.Select(x => $"<@{x.Key}> declared {x.Value.Item1} for {x.Value.Item2}"));

    List<string> judgmentList = new();
    List<(ulong user, int Score)> scores = new();

    foreach (var submission in submissionsAsDeclared.Select(s => (User: s.Key, Word: s.Value.Word, Length: s.Value.Length)).OrderBy(s => s.Length))
    {
      IEnumerable<char> mLetters = LetterFunctions.MissingLetters(Letters, submission.Word);

      if (submission.Length != submission.Word.Length)
      {
        judgmentList.Add($"<@{submission.User}>, {submission.Word} isn't {submission.Length} letter{(submission.Length != 1 ? "s" : "")} long.");
        scores.Add((submission.User, 0));
      }
      else if (mLetters.Any())
      {
        var letterCounts = mLetters
          .GroupBy(x => x)
          .OrderBy(g => g.Key)
          .Select(g => (Letter: g.Key, Count: g.Count(), Another: Letters.Contains(g.Key)))
          .Select(g => (g.Another ? "another " : "") +
            (g.Count > 1 ? $"{g.Count} {g.Letter}'s" : (g.Another) ? $"{g.Letter}" :
              (g.Letter switch
              {
                'A' or 'E' or 'F' or 'H' or 'I' or 'L' or 'M' or 'N' or 'O' or 'R' or 'S' or 'X' => $"an {g.Letter}",
                _ => $"a {g.Letter}"
              })));

        string lettersString = letterCounts.Count() switch
        {
          1 => letterCounts.First(),
          2 => $"{letterCounts.First()} and {letterCounts.Last()}",
          > 2 => $"{string.Join(", ", letterCounts.SkipLast(1))}, and {letterCounts.Last()}",
          _ => ""
        };

        judgmentList.Add($"<@{submission.User}>, you need {lettersString} to make {submission.Word}.");
        scores.Add((submission.User, 0));
      }
      else if (!LetterFunctions.InWordList(submission.Word))
      {
        judgmentList.Add($"<@{submission.User}>, {submission.Word} isn't in the dictionary.");
        scores.Add((submission.User, 0));
      }
      else if (submission.Length == 9)
      {
        judgmentList.Add($"<@{submission.User}>, congratulations, {submission.Word} is a valid 9-letter word!");
        scores.Add((submission.User, 18));
      }
      else
      {
        judgmentList.Add($"<@{submission.User}>, {submission.Word} is a valid word for {submission.Length}");
        scores.Add((submission.User, submission.Length));
      }
    }

    string judgmentMessage = string.Join("\n", judgmentList);

    scores = scores.OrderByDescending(x => x.Score).ToList();

    var starWords = LetterFunctions.WordsForLetters(Letters).MaxManyBy(x => x.Length).ToList();
    int starScore = starWords.First().Length;
    starScore = (starScore == 9) ? 18 : starScore;
    string scoresMessage;
    bool starScored = scores.FirstOrDefault().Score == starScore;

    if (scores.FirstOrDefault().Score == 0) scoresMessage = "Nobody scored any points this round.";
    else
    {
      var vsScores = scores.MaxManyBy(x => x.Score);
      var vsScoresText = vsScores.Select(x => $"- <@{x.user}>: {x.Score}{(x.Score == starScore ? (starScore == 18 ? " 🌟" : " ⭐") : "")}");
      scoresMessage = $"**Vs and Solo Scores**\n{string.Join("\n", vsScoresText)}";
      var zeroScores = scores.Where(x => x.Score == 0);
      var zeroScoresList = zeroScores.Select(x => $"- <@{x.user}>");
      var soloScores = scores.Except(vsScores).Except(zeroScores).Select(x => $"- <@{x.user}>: {x.Score}");
      if (soloScores.Any()) scoresMessage += $"\n**Solo scores only**:\n{string.Join("\n", soloScores)}";
      if (zeroScores.Any()) scoresMessage += $"\n**Zero points, vs or solo:**\n{string.Join("\n", zeroScoresList)}";
    }

    string starMessage = "";
    string starLength = starScore switch
    {
      18 => "nine",
      8 => "eight",
      7 => "seven",
      6 => "six",
      5 => "five",
      4 => "four",
      3 => "three",
      2 => "two",
      _ => starScore.ToString()
    };
    var starWordsExcludingPlayers = starWords.Except(submissionsAsDeclared.Select(x => x.Value.Word));
    string randomStarWord = starWordsExcludingPlayers.Skip((int)Random.Shared.NextInt64(starWordsExcludingPlayers.Count())).FirstOrDefault("");

    if (!starScored)
    {
      if (starWords.Count > 1)
      {
        starMessage = $"There were {starWords.Count} {starLength}{(starScore == 6 ? "es" : "s")} available, including {randomStarWord}.";
      }
      else
      {
        starMessage = $"There was {(starScore == 8 ? "an" : "a")} {starLength} available, which was {randomStarWord}.";
      }
    }
    else if (starWordsExcludingPlayers.Count() > 1)
    {
      starMessage = $"In addition to players' guesses, there were {starWordsExcludingPlayers.Count()} other {starLength}{(starScore == 6 ? "es" : "s")} available, including {randomStarWord}";
    }
    else if (starWordsExcludingPlayers.Count() == 1)
    {
      starMessage = $"In addition to players' guesses, there was one other {starLength} available, which was {randomStarWord}.";
    }
    else if (starWords.Count > 1)
    {
      starMessage = $"The players found all of the available {starLength}{(starScore == 6 ? "es" : "s")}. Well done!";
    }
    else if (starWords.Count == 1)
    {
      starMessage = $"The players found the only available {starLength}. Well done!";
    }

    DiscordMessageBuilder builder = new DiscordMessageBuilder()
      .AddEmbed(new DiscordEmbedBuilder()
        .WithTitle($"Round {Game.Rounds.Count} results")
        .AddField("Given letters", givenLetters)
        .AddField("Words submitted", wordsSubmitted)
        .AddField("Judgments", judgmentMessage)
        .AddField("Scores", scoresMessage)
        .AddField("Stars", starMessage)
      );

    await Game.Thread.SendMessageAsync(builder);

    SetState(GameState.RoundEnded);
    Message = null;
  }
}

public enum LetterDeck
{
  Consonants = 0,
  Vowels = 1
}

public static class LetterInteractionLogic
{
  public static async Task Draw(DiscordInteraction intr, LetterDeck which)
  {
    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsLettersRound)
        .And(Condition.AreLettersDrawing)
        .And(Condition.IsRoundControllerOrHost),
      out string reason, out ConditionResult result))
    {
      await intr.EphemeralResponse(reason);
      return;
    }

    CountdownGame game = result.Game;
    LettersRound round = (LettersRound)result.Round;

    if (intr.Type == DiscordInteractionType.Component)
      await intr.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage);

    char newLetter = LetterDecks.Draw(game.ThreadId, which == LetterDeck.Vowels);
    round.Letters.Add(newLetter);

    await round.Message.ModifyAsync(round.GetMessageBuilder());

    if (intr.Type == DiscordInteractionType.ApplicationCommand)
      await intr.EphemeralResponse("Drawn!");

    if (round.Letters.Count == 9)
    {
      round.SetState(GameState.Declarations);
      await intr.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder()
        .WithContent("All letters are chosen! Submit your declarations now using `/letters declare`.\n"
          + $"<@{round.Controller}>, you should declare first!"));
    }
  }

  public static async Task ShowWords(DiscordInteraction intr, IEnumerable<char> letters)
  {
    if (intr.Type == DiscordInteractionType.Component)
      await intr.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage);

    DiscordEmbedBuilder embed = new();
    embed.Title = $"Words for {letters}";
    var words = LetterFunctions.WordsForLetters(letters);
    embed.Description = $"There are {words.Count()} words that can be made from the letters `{letters}`.";

    if (letters.Where(l => l switch { 'A' or 'E' or 'I' or 'O' or 'U' => true, _ => false }).Count() > 5)
      embed.Description += "\n\n⚠️ This is not a legal Countdown letters selection (too many vowels). Words with more than 5 vowels are not shown below.";
    if (letters.Where(l => l switch { 'A' or 'E' or 'I' or 'O' or 'U' => false, _ => true }).Count() > 6)
      embed.Description += "\n\n⚠️ This is not a legal Countdown letters selection (too many consonants). Words with more than 6 consonants are not shown below.";

    var wordsGroupedByLength = words.GroupBy(word => word.Length).OrderByDescending(g => g.Key);

    foreach (var group in wordsGroupedByLength)
    {
      int count = group.Count();
      int allWordsFit = 1026 / (group.Key + 2);
      int someWordsFit = 1010 / (group.Key + 2);

      if (count <= allWordsFit)
        embed.AddField($"{group.Key} letters ({group.Count()})", string.Join(", ", group));
      else
        embed.AddField($"{group.Key} letters ({group.Count()})", string.Join(", ", group.Take(someWordsFit)) + $", (... {count - someWordsFit} more)");
    }

    await intr.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder().AddEmbed(embed).AsEphemeral());
  }
}

[Command("letters")]
[Description("Letters round commands")]
public class LettersCommands
{
  [Command("start")]
  [Description("Starts a letters round")]
  public async Task StartLettersRound(SlashCommandContext ctx,
    [Description("The controller of this round")] DiscordUser controller = null,
    [Description("Force the round to change, even if another round wasn't finished")] bool force = false,
    [Description("Pre-populate the letters selection.")] string letters = null
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
    string error = null;

    LettersRound round = new(result.Game, null, contID);

    if (letters != null)
    {
      if (!Regex.IsMatch(letters.ToUpper().Replace(" ", ""), "^[A-Z]+$"))
      {
        error = $"`letters` must be only letters (and spaces); the input {letters} is not valid.\n" +
          "(The invalid input was ignored and the round was created without pre-picked letters. You may still add them with `/letters pick`.)";
      }
      else
      {
        letters = letters.ToUpper().Replace(" ", "");

        if (letters.Length > 9)
        {
          error = $"`letters` must be no more than 9 letters long; the input {letters} is too long.\n" +
            $"(The first nine letters, `{letters[0..9]}` were used.)";
          letters = letters[0..9];
        }

        round.Letters = letters.ToList();
      }
    }

    letters = letters ?? "";

    if (letters.Length == 9)
    {
      round.SetState(GameState.Declarations);
    }

    var builder = round.GetMessageBuilder();
    var message = await ctx.EditResponseAsync(builder);
    round.SetMessage(message);

    if (letters.Length == 9)
    {
      await ctx.FollowupAsync("All letters are chosen! Submit your declarations now using `/letters declare`.\n"
          + $"<@{round.Controller}>, you should declare first!");
    }

    if (error != null) await ctx.FollowupAsync(error, true);
  }

  [Command("draw")]
  [Description("Draws a card from one of the decks.")]
  public async Task DrawLetter(SlashCommandContext ctx,
    [Description("Which deck to draw from?")] LetterDeck deck
  )
    => await LetterInteractionLogic.Draw(ctx.Interaction, deck);

  [Command("add")]
  [Description("Adds specific letters.")]
  public async Task AddLetters(SlashCommandContext ctx,
    [Description("Which letters to add")] string letters
  )
  {
    DiscordInteraction intr = ctx.Interaction;

    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsLettersRound)
        .And(Condition.AreLettersDrawing)
        .And(Condition.IsRoundControllerOrHost
          .WithError("You are not the game's host!"))
        .And(Condition.IsGameHost
          .AppendError(" The controller may only draw letters, not force specifics.")),
      out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    LettersRound round = (LettersRound)result.Round;

    if (!Regex.IsMatch(letters.ToUpper().Replace(" ", ""), "^[A-Z]+$"))
    {
      await intr.EphemeralResponse($"`letters` must be only letters (and spaces); the input {letters} is not valid.");
      return;
    }

    letters = letters.ToUpper().Replace(" ", "");

    int total = letters.Length + round.Letters.Count;

    if (letters.Length + round.Letters.Count > 9)
    {
      int added = 9 - round.Letters.Count;

      await ctx.RespondAsync($"`letters`, plus the letters already drawn, must be no more than 9 letters long; the input {letters} is too long.\n" +
          $"(The first {added} letters of your input, `{letters[0..added]}` were used.)", true);
      letters = letters[0..added];
    }

    round.Letters.AddRange(letters);

    await round.Message.ModifyAsync(round.GetMessageBuilder());

    if (round.Letters.Count == 9)
    {
      round.SetState(GameState.Declarations);

      string message = "All letters are chosen! Submit your declarations now using `/letters declare`.\n"
          + $"<@{round.Controller}>, you should declare first!";
      if (ctx.Interaction.ResponseState == DiscordInteractionResponseState.Unacknowledged)
        await ctx.RespondAsync(message);
      else
        await ctx.FollowupAsync(message);
    }

    if (ctx.Interaction.ResponseState != DiscordInteractionResponseState.Replied)
      await ctx.RespondAsync("Letters added!");
  }

  [Command("clear")]
  [Description("Clear the letters of the current round.")]
  public async Task ClearLetters(SlashCommandContext ctx)
  {
    DiscordInteraction intr = ctx.Interaction;

    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.CanRoundSetup)
        .And(Condition.AreLettersDrawing)
        .And(Condition.IsRoundControllerOrHost
          .WithError("You're not the host of this game!"))
        .And(Condition.IsGameHost
          .AppendError(" The controller may only draw letters, not force specifics.")),
      out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason);
      return;
    }

    await intr.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage);

    LettersRound round = (LettersRound)result.Round;

    round.Letters.Clear();
    if (round.State == GameState.Declarations) round.SetState(GameState.Setup);

    await round.Message.ModifyAsync(round.GetMessageBuilder());
  }

  [Command("declare")]
  [Description("Submits a declaration for the given letters")]
  public async Task DeclareWord(SlashCommandContext ctx,
    [Description("The length of the word being declared")] int length,
    [Description("The word being declared")] string word
  )
  {
    DiscordInteraction intr = ctx.Interaction;

    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsLettersRound)
        .And(Condition.IsPlayerInRound)
        .And(Condition.CanRoundDeclare),
      out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    LettersRound round = (LettersRound)result.Round;

    word = word.ToUpper().Trim();

    if (round.State == GameState.Declarations) round.SetState(GameState.DeclarationsMade);

    ulong userId = ctx.User.Id;

    if (round.Submissions.ContainsKey(userId))
    {
      await ctx.RespondAsync("You've already submitted a word! You can use `/letters check` if you're curious about another (but it should wait until everyone's submitted).", true);
      return;
    }

    // Is the declared length valid?
    if (length != word.Length)
    {
      round.InvalidSubmissions[userId] = (word, length);
      round.Submissions[userId] = "";
    }
    // Are all the letters used present?
    else if (LetterFunctions.MissingLetters(round.Letters, word).Any())
    {
      round.InvalidSubmissions[userId] = (word, length);
      round.Submissions[userId] = "";
    }
    else
    {
      round.Submissions[userId] = word;
    }

    await ctx.RespondAsync($"Your submission of `{word}` for {length} has been recorded.", true);
    await ctx.FollowupAsync($"<@{userId}> has declared a {length}-letter word.");

    if (!(round.Players.Except(round.Submissions.Keys).Any()))
    {
      await round.HandleEndOfRound();
    }
  }

  [Command("list")]
  [Description("Returns all valid words that can be made from the given letters")]
  public async Task ListWords(SlashCommandContext ctx,
    [Description("The given letters for the round")] string letters = null
  )
  {
    if (letters == null)
    {
      if (!Condition.Check(ctx.Interaction,
        Condition.IsGameThread
          .And(Condition.AnyRoundExists)
          .And(Condition.IsLettersRound)
          .And(Condition.AreLettersDrawing.Inverse("Letters are still drawing")),
        out string reason, out ConditionResult result
      ))
      {
        await ctx.RespondAsync($"{reason} You may specify the `letters` argument to check for a certain set anyway.", true);
        return;
      }
      else
      {
        letters = ((LettersRound)result.Round).Letters.FormString();
      }
    }
    else if (!Regex.IsMatch(letters.ToUpper().Replace(" ", ""), "^[a-z]+$"))
    {
      await ctx.RespondAsync($"`letters` must be only letters (and spaces); the input {letters} is not valid.", true);
      return;
    }

    if (Condition.Check(ctx.Interaction,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsLettersRound)
        .And(Condition.IsRoundInProgress),
      out _, out _))
    {
      await ctx.RespondAsync("This command can't be used during a letters round until the guesses are all submitted!", true);
      return;
    }

    letters = letters.ToUpper().Replace(" ", "");

    if (letters.Length < 2)
    {
      await ctx.RespondAsync($"`letters` must be 2-9 letters long; the input {letters} is too short.", true);
      return;
    }

    if (letters.Length > 9)
    {
      await ctx.RespondAsync($"`letters` must be 2-9 letters long; the input {letters} is too long.", true);
      return;
    }

    var words = LetterFunctions.WordsForLetters(letters).ToList();

    DiscordMessageBuilder response = new DiscordMessageBuilder()
      .WithContent($"Words for {letters}: There are {words.Count()} words that can be made from these letters.")
      .AddComponents(
        new DiscordButtonComponent(DiscordButtonStyle.Primary, $"letters-showwords-{letters}", "Show")
      );
    await ctx.RespondAsync(response);
    await LetterInteractionLogic.ShowWords(ctx.Interaction, letters);
  }

  [Command("check")]
  [Description("Checks whether a given word is valid for the given submission")]
  public async Task CheckWord(SlashCommandContext ctx,
    [Description("The word to check against these letters")] string word,
    [Description("The given letters for the round")] string letters = null,
    [Description("The declared length of the guess")] int length = 0
  )
  {
    if (letters == null)
    {
      if (!Condition.Check(ctx.Interaction,
        Condition.IsGameThread
          .And(Condition.AnyRoundExists)
          .And(Condition.IsLettersRound)
          .And(Condition.AreLettersDrawing.Inverse("Letters are still drawing")),
        out string reason, out ConditionResult result
      ))
      {
        await ctx.RespondAsync($"{reason} You may specify the `letters` argument to check for a certain set anyway.", true);
        return;
      }
      else
      {
        letters = ((LettersRound)result.Round).Letters.FormString();
      }
    }
    else if (!Regex.IsMatch(letters.ToUpper().Replace(" ", ""), "^[A-Z]+$"))
    {
      await ctx.RespondAsync($"`letters` must be only letters (and spaces); the input {letters} is not valid.", true);
      return;
    }

    if (Condition.Check(ctx.Interaction,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsLettersRound)
        .And(Condition.IsRoundInProgress),
      out _, out _))
    {
      await ctx.RespondAsync("This command can't be used during a letters round until the guesses are all submitted!", true);
      return;
    }

    await ctx.DeferResponseAsync();

    letters = letters.ToUpper().Replace(" ", "");

    if (length == 0) length = word.Length;

    bool isValidWord = true;
    List<string> whyNot = new();

    if (!LetterFunctions.InWordList(word))
    {
      isValidWord = false;
      whyNot.Add("not in dictionary");
    }

    char[] chrs = LetterFunctions.MissingLetters(letters, word).ToArray();
    if (chrs.Length > 0)
    {
      isValidWord = false;
      whyNot.Add($"missing {string.Join(", ", chrs)}");
    }

    if (length != word.Length)
    {
      isValidWord = false;
      whyNot.Add($"misdeclared length {length} for a word of length {word.Length}");
    }

    if (isValidWord)
      await ctx.EditResponseAsync($"`{word}` is a valid word with the letters {string.Join("", letters)}.");
    else
      await ctx.EditResponseAsync($"`{word}` is not a valid word with the letters {string.Join("", letters)} ({string.Join("; ", whyNot)}).");
  }

  [Command("pass")]
  [Description("Leave the round with no guess")]
  public async Task Pass(SlashCommandContext ctx)
  {
    var intr = ctx.Interaction;

    if (!Condition.Check(intr,
      Condition.IsGameThread
        .And(Condition.AnyRoundExists)
        .And(Condition.IsLettersRound)
        .And(Condition.IsPlayerInRound)
        .And(Condition.CanRoundDeclare),
      out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    await ctx.RespondAsync($"<@{ctx.User.Id}> has passed.");

    LettersRound round = (LettersRound)result.Round;

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
        .And(Condition.IsLettersRound)
        .And(Condition.IsGameHost),
    out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    LettersRound round = (LettersRound)result.Round;

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
}

[Command("dictionary")]
public static class DictionaryCommands
{
  [Command("add")]
  public static async Task AddToDictionary(SlashCommandContext ctx,
    [Description("The word to add")] string word
  )
  {
    if (!Condition.Check(ctx.Interaction,
      Condition.IsBotOwner,
    out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    word = word.ToUpper();
    var words = LetterFunctions.BaseWords;
    var exc = LetterFunctions.Exceptions;
    var add = LetterFunctions.Additions;

    if (words.Contains(word))
    {
      add.Remove(word);
      if (exc.Contains(word))
      {
        exc.Remove(word);
        await ctx.RespondAsync($"{word} has been re-added to the dictionary.");
      }
      else
      {
        await ctx.RespondAsync($"{word} is already in the dictionary.", true);
      }
    }
    else
    {
      exc.Remove(word); // In case updates mess it up
      if (add.Contains(word))
      {
        await ctx.RespondAsync($"{word} is already added to the dictionary.", true);
      }
      else
      {
        add.Add(word);
        await ctx.RespondAsync($"{word} has been added to the dictionary.");
      }
    }
  }

  [Command("remove")]
  public static async Task RemoveFromDictionary(SlashCommandContext ctx,
    [Description("The word to remove")] string word
  )
  {
    if (!Condition.Check(ctx.Interaction,
      Condition.IsBotOwner,
    out string reason, out ConditionResult result))
    {
      await ctx.RespondAsync(reason, true);
      return;
    }

    word = word.ToUpper();
    var words = LetterFunctions.BaseWords;
    var exc = LetterFunctions.Exceptions;
    var add = LetterFunctions.Additions;

    if (words.Contains(word))
    {
      add.Remove(word);
      if (exc.Contains(word))
      {
        await ctx.RespondAsync($"{word} is already not in the dictionary.", true);
      }
      else
      {
        exc.Add(word);
        await ctx.RespondAsync($"{word} has been removed from the dictionary.");
      }
    }
    else
    {
      exc.Remove(word); // In case updates mess it up
      if (add.Contains(word))
      {
        add.Remove(word);
        await ctx.RespondAsync($"{word} has been cleared from the dictionary.");
      }
      else
      {
        await ctx.RespondAsync($"{word} is already not in the dictionary.", true);
      }
    }
  }
}

public static class LetterFunctions
{
  internal static AVLTreeSet<string> BaseWords;
  internal static AVLTreeSet<string> Additions;
  internal static AVLTreeSet<string> Exceptions;

  public static IEnumerable<string> WordsForLetters(string letters)
    => WordsForLetters((IEnumerable<char>)(letters.ToUpper()));

  public static IEnumerable<string> WordsForLetters(IEnumerable<char> letters)
  {
    Dictionary<char, int> inputBuckets = letters
      .GroupBy(l => l)
      .ToDictionary(g => g.Key, g => g.Count());
    Dictionary<char, int> emptyBuckets = inputBuckets
      .Select(kvp => kvp.Key)
      .ToDictionary(key => key, key => 0);

    foreach (string word in BaseWords)
    {
      Dictionary<char, int> wordBuckets = new(emptyBuckets);
      foreach (char chr in word)
      {
        if (wordBuckets.ContainsKey(chr))
        {
          wordBuckets[chr]++;
          if (wordBuckets[chr] > inputBuckets[chr]) goto skipWord;
        }
        else
        {
          goto skipWord;
        }
      }

      yield return word;

    skipWord:;
    }
  }

  public static IEnumerable<char> MissingLetters(IEnumerable<char> letters, string word)
  {
    word = word.ToUpper();

    Dictionary<char, int> inputBuckets = letters
      .Select(char.ToUpper)
      .GroupBy(l => l)
      .ToDictionary(g => g.Key, g => g.Count());
    Dictionary<char, int> wordBuckets = inputBuckets
      .Select(kvp => kvp.Key)
      .ToDictionary(key => key, key => 0);

    foreach (char chr in word)
    {
      if (wordBuckets.ContainsKey(chr))
      {
        wordBuckets[chr]++;
        if (wordBuckets[chr] > inputBuckets[chr]) yield return chr;
      }
      else
      {
        yield return chr;
      }
    }
  }

  public static bool InWordList(string word)
    => (BaseWords.Contains(word.ToUpper()) && !Exceptions.Contains(word.ToUpper())) || Additions.Contains(word.ToUpper());
}

public static class LetterButtonEvents
{
  public static async Task DrawVowelButtonPressed(DiscordClient discord, ComponentInteractionCreateEventArgs args)
  {
    if (args.Id != "letters-draw-vowel") return;
    await LetterInteractionLogic.Draw(args.Interaction, LetterDeck.Vowels);
  }

  public static async Task DrawConsonantButtonPressed(DiscordClient discord, ComponentInteractionCreateEventArgs args)
  {
    if (args.Id != "letters-draw-consonant") return;
    await LetterInteractionLogic.Draw(args.Interaction, LetterDeck.Consonants);
  }

  public static async Task ShowWordsButtonPressed(DiscordClient discord, ComponentInteractionCreateEventArgs args)
  {
    if (!args.Id.StartsWith("letters-showwords-")) return;
    await LetterInteractionLogic.ShowWords(args.Interaction, args.Id[18..]);
  }
}