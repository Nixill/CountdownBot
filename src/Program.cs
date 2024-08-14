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
    string botToken = File.ReadAllText("cfg/token.cfg");

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

    ulong debugGuildId = 0L;
    if (File.Exists("cfg/debug_guild.cfg"))
    {
      debugGuildId = ulong.Parse(File.ReadAllText("cfg/debug_guild.cfg").Trim());
      Console.WriteLine("Debugging!");
    }

    IServiceProvider serviceProvider = new ServiceCollection().AddLogging(x => x.AddConsole()).BuildServiceProvider();

    Commands = Discord.UseCommands(
      new CommandsConfiguration()
      {
        DebugGuildId = debugGuildId,
        ServiceProvider = serviceProvider,
        RegisterDefaultCommandProcessors = false
      }
    );

    EventHandlers.RegisterEvents(Discord, Commands);

    SlashCommandProcessor processor = new();
    await Commands.AddProcessorAsync(processor);

    Commands.AddCommands(typeof(CountdownBotMain).Assembly);

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