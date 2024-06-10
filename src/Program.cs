using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.EventArgs;
using DSharpPlus.Commands.Processors.SlashCommands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nixill.CalcLib.Modules;
using NodaTime;

namespace Nixill.Discord.Countdown;

public class CountdownBotMain
{
  internal static Instant Now => SystemClock.Instance.GetCurrentInstant();
  internal static DiscordClient Discord;
  internal static CommandsExtension Commands;
  internal static CancellationTokenSource QuitTokenSource;

  internal static ulong OwnerID;

  static void Main(string[] args) => MainAsync().GetAwaiter().GetResult();

  public static async Task MainAsync()
  {
    // Let's get the bot set up
#if DEBUG
    Console.WriteLine("Debug mode active");
    string botToken = File.ReadAllText("cfg/debug_token.cfg");
#else
    Console.WriteLine("Debug mode not active");
    string botToken = File.ReadAllText("cfg/token.cfg");
#endif

    MainModule.LoadBinaryDivide();
    MainModule.LoadBinaryMinus();
    MainModule.LoadBinaryPlus();
    MainModule.LoadBinaryTimes();

    OwnerID = ulong.Parse(File.ReadAllText("cfg/owner"));

    Discord = new DiscordClient(new DiscordConfiguration()
    {
      Token = botToken,
      Intents = SlashCommandProcessor.RequiredIntents,
      MinimumLogLevel = Microsoft.Extensions.Logging.LogLevel.Information
    });

    IServiceProvider serviceProvider = new ServiceCollection().AddLogging(x => x.AddConsole()).BuildServiceProvider();

    Commands = Discord.UseCommands(
      new CommandsConfiguration()
      {
#if DEBUG
        DebugGuildId = 1243624266935828642L,
#else
        DebugGuildId = 0L,
#endif
        ServiceProvider = serviceProvider,
        RegisterDefaultCommandProcessors = false
      }
    );

    EventHandlers.RegisterEvents(Discord, Commands);

    SlashCommandProcessor processor = new();
    await Commands.AddProcessorAsync(processor);

#if DEBUG
    Commands.AddCommands(typeof(CountdownBotMain).Assembly, 1243624266935828642L);
#else
    Commands.AddCommands(typeof(CountdownBotMain).Assembly);
#endif

    await Discord.ConnectAsync();

    QuitTokenSource = new CancellationTokenSource();

    try
    {
      await Task.Delay(-1, QuitTokenSource.Token);
    }
    catch (TaskCanceledException)
    {
      SaveAndLoad.SaveToFiles();
    }
  }

  internal static Task OnCommandErrored(CommandsExtension sender, CommandErroredEventArgs args)
  {
    Console.WriteLine(args.Exception);
    return Task.CompletedTask;
  }
}