using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using Nixill.Utils;
using Interaction = DSharpPlus.Entities.DiscordInteraction;

namespace Nixill.Discord.Countdown;

public class ConditionResult
{
  public CountdownGame Game = null;
  public ulong HostId = 0;
  public CountdownRound Round = null;
  public ulong ControllerId = 0;
  public Condition FailedCondition = null;
}

public class Condition
{
  internal Func<Interaction, ConditionResult, bool> Predicate;
  internal string ErrorMessage;

  public readonly string ConditionID;

  internal Condition(Func<Interaction, ConditionResult, bool> pred, string error, string id)
  {
    Predicate = pred;
    ErrorMessage = error;
    ConditionID = id;
  }

  public override bool Equals(object obj)
  {
    if (!(obj is Condition cond)) return false;
    return (this.ConditionID == cond.ConditionID);
  }

  public override int GetHashCode()
  {
    return ConditionID.GetHashCode();
  }

  public static bool operator ==(Condition left, Condition right) => left.Equals(right);
  public static bool operator !=(Condition left, Condition right) => !left.Equals(right);
  public static bool operator ==(Condition left, string right) => left.ConditionID == right;
  public static bool operator !=(Condition left, string right) => left.ConditionID != right;
  public static bool operator ==(string left, Condition right) => left == right.ConditionID;
  public static bool operator !=(string left, Condition right) => left != right.ConditionID;

  public static bool Check(Interaction intr, Condition condition, out string reason, out ConditionResult result)
    => Check(intr, EnumerableUtils.Of(condition), out reason, out result);

  public static bool Check(Interaction intr, IEnumerable<Condition> conditions, out string reason, out ConditionResult result)
  {
    result = new();

    foreach (Condition cond in conditions)
    {
      if (!cond.Predicate(intr, result))
      {
        reason = cond.ErrorMessage;
        result.FailedCondition = cond;
        return false;
      }
    }

    reason = null;
    return true;
  }

  public static readonly Condition IsGameThread = new(
    (i, c) => (c.Game = CountdownGameController.GetOrNull(i.ChannelId)) != null,
    "This isn't a game thread!",
    "IsGameThread"
  );

  public static readonly Condition IsNotGameThread = IsGameThread.Inverse(
    "This is already a game thread!"
  );

  // Prerequisite: IsGameThread
  public static readonly Condition IsGameHost = new(
    (i, c) => (c.HostId = c.Game.Host) == i.User.Id,
    "You aren't the host of this game!",
    "IsGameHost"
  );

  // Prerequisite: IsGameThread
  public static readonly Condition IsGameOver = new(
    (i, c) => c.Game.State == GameState.GameEnded,
    "This game hasn't ended yet!",
    "IsGameOver"
  );

  // Prerequisite: IsGameThread
  public static readonly Condition IsGameNotOver = IsGameOver.Inverse(
    "This game has already ended!"
  );

  // Prerequisite: IsGameThread
  public static readonly Condition IsPlayerInGame = new(
    (i, c) => c.Game.ActivePlayers.Contains(i.User.Id),
    "You are not in this game!",
    "IsPlayerInGame"
  );

  // Prerequisite: IsGameThread
  public static readonly Condition IsPlayerNotInGame = IsPlayerInGame.Inverse(
    "You are already in this game!"
  );

  public static readonly Condition IsNotPrivateChannel = new(
    (i, c) => !i.Channel.IsPrivate,
    "This bot only works in servers!",
    "IsNotPrivateChannel"
  );

  public static readonly Condition IsBotOwner = new(
    (i, c) => i.User.Id == CountdownBotMain.OwnerID,
    "Only the bot's owner may use this command!",
    "IsBotOwner"
  );

  // Prerequisite: IsGameThread
  public static readonly Condition AnyRoundExists = new(
    (i, c) => (c.Round = c.Game.Rounds.LastOrDefault()) != null,
    "No rounds has been started yet!",
    "AnyRoundExists"
  );

  // Prerequisite: IsGameThread
  public static readonly Condition NoRoundInProgress = new(
    (i, c) => c.Game.State == GameState.Roundless || c.Game.State == GameState.RoundEnded,
    "Another round is already in progress!",
    "NoRoundInProgress"
  );

  // Prerequisite: IsGameThread
  public static readonly Condition IsRoundInProgress = new(
    (i, c) => c.Game.State switch { GameState.Setup or GameState.Declarations or GameState.DeclarationsMade => true, _ => false },
    "No round is currently in progress!",
    "IsRoundInProgress"
  );

  // Prerequisites: IsGameThread, AnyRoundExists
  public static readonly Condition IsRoundSetup = new(
    (i, c) => c.Round.State == GameState.Setup,
    "The current round isn't in setup!",
    "IsRoundSetup"
  );

  // Prerequisites: IsGameThread, AnyRoundExists
  public static readonly Condition IsPlayerInRound = new(
    (i, c) => c.Round.Participants.Contains(i.User.Id),
    "You're not in this round!",
    "IsPlayerInRound"
  );

  // Prerequisites: IsGameThread, AnyRoundExists
  public static readonly Condition CanRoundSetup = new(
    (i, c) => c.Round.State == GameState.Setup || c.Round.State == GameState.Declarations,
    "The current round cannot revert to setup!",
    "CanRoundSetup"
  );

  // Prerequisites: IsGameThread, AnyRoundExists
  public static readonly Condition CanRoundDeclare = new(
    (i, c) => c.Round.State == GameState.Declarations || c.Round.State == GameState.DeclarationsMade,
    "The current round is not accepting declarations!",
    "CanRoundDeclare"
  );

  // Prerequisites: IsGameThread, AnyRoundExists
  public static readonly Condition IsRoundControllerOrHost = new(
    (i, c) => ((c.HostId = c.Game.Host) == i.User.Id) | ((c.ControllerId = c.Round.Controller) == i.User.Id),
    "You are neither the game's host nor the round's controller!",
    "IsRoundControllerOrHost"
  );

  // Prerequisites: IsGameThread, AnyRoundExists
  public static readonly Condition IsLettersRound = new(
    (i, c) => c.Round is LettersRound,
    "There isn't a letters round in progress!",
    "IsLettersRound"
  );

  // Prerequisites: IsGameThread, AnyRoundExists
  // (should only be used in letters rounds but that's not *technically* a
  // prerequisite)
  public static readonly Condition AreLettersDrawing = IsRoundSetup
    .WithError("The current round isn't in drawing phase!");

  // Prerequisites: IsGameThread, AnyRoundExists, IsLettersRound
  public static readonly Condition AreVowelsAvailable = new(
    (i, c) => ((LettersRound)c.Round).Vowels < 5,
    "No more vowels can be drawn!",
    "AreVowelsAvailable"
  );

  // Prerequisites: IsGameThread, AnyRoundExists, IsLettersRound
  public static readonly Condition AreConsonantsAvailable = new(
    (i, c) => ((LettersRound)c.Round).Consonants < 5,
    "No more consonants can be drawn!",
    "AreConsonantsAvailable"
  );

  // Prerequisites: IsGameThread, AnyRoundExists
  public static readonly Condition IsNumbersRound = new(
    (i, c) => (c.Round is NumbersRound),
    "There isn't a numbers round in progress!",
    "IsNumbersRound"
  );

  // Prerequisites: IsGameThread, AnyRoundExists, IsNumbersRound
  public static readonly Condition AreNumbersDrawing = new(
    (i, c) => ((NumbersRound)c.Round).Numbers.Count == 0,
    "Numbers have already been drawn!",
    "AreNumbersDrawing"
  );

  // Prerequisites: IsGameThread, AnyRoundExists, IsNumbersRound
  public static readonly Condition IsTargetDrawing = new(
    (i, c) => ((NumbersRound)c.Round).Target == 0,
    "A target has already been chosen!",
    "IsTargetDrawing"
  );

  // Prerequisites: IsGameThread, AnyRoundExists, IsNumbersRound
  public static readonly Condition NotAwaitingNumbersSubmission = new(
    (i, c) => ((NumbersRound)c.Round).Submissions.ContainsKey(i.User.Id) || !c.Round.Players.Contains(i.User.Id),
    "You haven't submitted a declared answer yet!",
    "NotAwaitingNumbersSubmission"
  );
}

public static class ConditionExtensions
{
  public static Condition Inverse(this Condition of, string newMessage)
    => new Condition((i, c) => !of.Predicate(i, c), newMessage, of.ConditionID + "_INVERSE");

  public static Condition WithError(this Condition of, string newMessage)
    => new Condition(of.Predicate, newMessage, of.ConditionID);

  public static Condition AppendError(this Condition of, string addedMessage)
    => new Condition(of.Predicate, of.ErrorMessage + addedMessage, of.ConditionID);

  public static IEnumerable<Condition> And(this Condition of, Condition next)
    => EnumerableUtils.Of(of, next);

  public static IEnumerable<Condition> And(this IEnumerable<Condition> of, Condition next)
    => of.Append(next);
}