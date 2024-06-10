using System.Collections.Concurrent;
using NodaTime;

namespace Nixill.Discord.Countdown;

public static class DeleteQueue
{
  private static ConcurrentQueue<DeletionTask> Tasks = new();

  private static Duration WaitTime = Duration.FromMinutes(5); // for testing; will be set to FromDays(1) later

  private static Instant Now => SystemClock.Instance.GetCurrentInstant();

  public static async void Begin()
  {
    while (true)
    {
      // Console.WriteLine("Delete queue cycled.");
      if (Tasks.IsEmpty)
      {
        // Console.WriteLine($"Nothing in delete queue; waiting until {Now + WaitTime}.");
        Thread.Sleep(WaitTime.ToTimeSpan());
      }
      else
      {
        // Console.WriteLine("Item found in delete queue.");
        if (Tasks.TryDequeue(out var task))
        {
          Duration timeToWait = (task.QueuedAt - Now + WaitTime + Duration.FromSeconds(1));
          if (timeToWait > Duration.Zero)
          {
            // Console.WriteLine($"Game {task.Game.ThreadId} in delete queue. Waiting until {Now + timeToWait}...");
            Thread.Sleep((timeToWait).ToTimeSpan());
            // Console.WriteLine($"Wait complete.");
          }
          else
          {
            // Console.WriteLine($"Game {task.Game.ThreadId} in delete queue. Wait time already passed, continuing...");
          }

          var game = task.Game;

          if (game.LastActivity + WaitTime < Now)
          {
            // Console.WriteLine($"Deleting game {task.Game.ThreadId}...");
            if (game.State != GameState.GameEnded) await game.HandleEndOfGame();
            CountdownGameController.GamesInProgress.Remove(game.ThreadId, out _);
          }
          // else
          // {
          // Console.WriteLine($"Time not passed yet. Discarding deletion task.");
          // }
        }
        else
        {
          // Console.WriteLine("Couldn't get item from queue. Waiting one second...");
          Thread.Sleep(1000);
        }
      }
    }
  }

  internal static void AddAll(IEnumerable<DeletionTask> tasks)
  {
    foreach (var task in tasks)
    {
      Tasks.Enqueue(task);
    }
  }

  internal static void Add(CountdownGame game)
  {
    Tasks.Enqueue(new() { QueuedAt = Now, Game = game });
  }

  internal static void Clear()
  {
    Tasks.Clear();
  }
}

internal struct DeletionTask
{
  public Instant QueuedAt;
  public CountdownGame Game;
}