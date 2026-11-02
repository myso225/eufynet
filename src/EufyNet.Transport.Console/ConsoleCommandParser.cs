using EufyNet.Core;
using EufyNet.P2P;

namespace EufyNet.Transport.Console;

/// <summary>What a console line means: a command, quit, or text to show.</summary>
internal readonly record struct ConsoleInput(BridgeCommand? Command = null, bool Quit = false, string? Help = null);

/// <summary>Turns stdin lines into <see cref="BridgeCommand"/>s. While the login waits, a bare line is the code / captcha.</summary>
internal static class ConsoleCommandParser
{
    private static readonly HashSet<string> Keywords = ["quit", "exit", "status", "info", "clips", "picture", "mode", "code", "help"];

    public static string HelpText { get; } =
        $"Commands: status | info [station] | clips [camera] | picture [camera] | mode <{string.Join("|", Enum.GetNames<GuardMode>()).ToLowerInvariant()}> [station] | quit";

    public static ConsoleInput Parse(string line, LoginState? login, string id)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keyword = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        if (login is { NeedsVerifyCode: true } && !Keywords.Contains(keyword))
            return parts.Length == 0
                ? new ConsoleInput(new BridgeCommand(id, BridgeCommands.ResendCode))
                : new ConsoleInput(VerifyCode(id, line.Trim()));
        if (login is { NeedsCaptcha: true, Captcha: { } captcha } && parts.Length > 0 && !Keywords.Contains(keyword))
            return new ConsoleInput(new BridgeCommand(id, BridgeCommands.Captcha, Args: new Dictionary<string, string>
            {
                ["id"] = captcha.CaptchaId,
                ["answer"] = line.Trim(),
            }));

        return keyword switch
        {
            "" => default,
            "quit" or "exit" => new ConsoleInput(Quit: true),
            "status" => new ConsoleInput(new BridgeCommand(id, BridgeCommands.Status)),
            "info" => new ConsoleInput(new BridgeCommand(id, BridgeCommands.Info, parts.ElementAtOrDefault(1))),
            "clips" => new ConsoleInput(new BridgeCommand(id, BridgeCommands.Clips,
                Args: parts.Length > 1 ? new Dictionary<string, string> { ["camera"] = string.Join(' ', parts[1..]) } : null)),
            "picture" => new ConsoleInput(new BridgeCommand(id, BridgeCommands.Picture,
                Args: parts.Length > 1 ? new Dictionary<string, string> { ["camera"] = string.Join(' ', parts[1..]) } : null)),
            "mode" when parts.Length >= 2 => new ConsoleInput(new BridgeCommand(id, BridgeCommands.SetGuardMode, parts.ElementAtOrDefault(2),
                new Dictionary<string, string> { ["mode"] = parts[1] })),
            "code" when parts.Length >= 2 => new ConsoleInput(VerifyCode(id, parts[1])),
            _ => new ConsoleInput(Help: HelpText),
        };
    }

    private static BridgeCommand VerifyCode(string id, string code) =>
        new(id, BridgeCommands.VerifyCode, Args: new Dictionary<string, string> { ["code"] = code });
}
