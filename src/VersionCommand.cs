using System.ComponentModel;
using System.Diagnostics;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Commands.Processors.SlashCommands.Metadata;
using DSharpPlus.Entities;

namespace Nixill.Discord.ShadowBot;

public static class VersionCommandClass
{
  static ValueTask<(bool Success, string Output, string Error)> Run(string prog, string args, bool throwOnError = true)
  {
    Process proc = new Process()
    {
      StartInfo = {
        FileName = prog,
        Arguments = args,
        WorkingDirectory = Directory.GetCurrentDirectory(),
        RedirectStandardError = true,
        RedirectStandardOutput = true
      }
    };

    proc.Start();
    proc.WaitForExit();

    return ValueTask.FromResult((proc.ExitCode == 0, proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd()));
  }

  [InteractionAllowedContexts(
    DiscordInteractionContextType.Guild,
    DiscordInteractionContextType.BotDM,
    DiscordInteractionContextType.PrivateChannel
  )]
  [InteractionInstallType(
    DiscordApplicationIntegrationType.UserInstall
  )]
  [Command("version")]
  [Description("Get the version of the bot")]
  public static async Task VersionCommand(SlashCommandContext ctx)
  {
    await ctx.DeferResponseAsync(true);

    var hash = Run("git", "rev-parse HEAD");
    var msg = Run("git", "log -1 --pretty=%B");
    var tag = Run("git", "describe --tags --abbrev=0");
    var unc = Run("git", "diff --quiet");

    string hashString = hash.Result.Output.Trim();
    string messageString = msg.Result.Output.Split('\n')[0].Trim();
    string versionString = tag.Result.Success ? $"version `{tag.Result.Output.Trim()}`" : "an untagged version";

    string response = $"I am running {versionString} on commit `{hashString}` (\"{messageString}\")"
      + (unc.Result.Success ? "." : ", plus further uncommitted changes.");

    DiscordMessageBuilder message = new DiscordMessageBuilder()
    {
      Content = response
    }.AddComponents(new DiscordLinkButtonComponent(
      $"https://github.com/StevenH237/ShadowBot/tree/{hashString}",
      "Browse repository here"
    ));

    await ctx.EditResponseAsync(message);
  }
}