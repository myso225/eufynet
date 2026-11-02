using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EufyNet.Cloud;
using EufyNet.Core;
using EufyNet.P2P;
using EufyNet.Transport.Console;

namespace EufyNet.Tests;

public class TransportBridgeTests
{
    [Fact]
    public async Task Driver_output_reaches_the_transport_in_order()
    {
        var driver = new FakeDriver();
        var transport = new FakeTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = new TransportBridge(driver, transport).RunAsync(cts.Token);

        driver.RaiseLogin(new LoginState(LoginStatus.Ok));
        driver.RaiseState(new StationState("S1", "Home", true, GuardMode.Home, null, EncryptionLevel.Level1, []));
        driver.RaiseEvent(new BridgeEvent(new LightSwitchEvent("S1", DateTimeOffset.Now, 0, false), "D0", "Front Door"));

        Assert.Equal(["login Ok", "state S1", "event LightSwitchEvent"], await transport.TakeAsync(3));
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Commands_are_executed_and_results_published()
    {
        var driver = new FakeDriver();
        var transport = new FakeTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = new TransportBridge(driver, transport).RunAsync(cts.Token);

        transport.Commands.Writer.TryWrite(new BridgeCommand("1", BridgeCommands.Status));
        transport.Commands.Writer.TryWrite(new BridgeCommand("2", BridgeCommands.Info, "S1"));

        Assert.Equal(["result 1 status", "result 2 info"], await transport.TakeAsync(2));
        Assert.Equal(["status", "info S1"], driver.Executed);

        // No more commands (stdin closed): events still flow until stopped.
        transport.Commands.Writer.Complete();
        driver.RaiseLogin(new LoginState(LoginStatus.Ok));
        Assert.Equal(["login Ok"], await transport.TakeAsync(1));
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task A_failing_transport_does_not_stop_the_bridge()
    {
        var driver = new FakeDriver();
        var transport = new FakeTransport { FailStates = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = new TransportBridge(driver, transport).RunAsync(cts.Token);

        driver.RaiseState(new StationState("S1", "Home", false, null, null, null, []));
        driver.RaiseLogin(new LoginState(LoginStatus.Ok));

        Assert.Equal(["login Ok"], await transport.TakeAsync(1));
        cts.Cancel();
        await run;
    }

    private sealed class FakeDriver : IEufyDriver
    {
        public event Action<LoginState>? LoginChanged;
        public event Action<StationState>? StateChanged;
        public event Action<BridgeEvent>? EventRaised;

        public List<string> Executed { get; } = [];

        public void RaiseLogin(LoginState s) => LoginChanged?.Invoke(s);
        public void RaiseState(StationState s) => StateChanged?.Invoke(s);
        public void RaiseEvent(BridgeEvent e) => EventRaised?.Invoke(e);

        public Task<BridgeCommandResult> ExecuteAsync(BridgeCommand command, CancellationToken cancellationToken)
        {
            Executed.Add($"{command.Name}{(command.StationSn is { } sn ? $" {sn}" : "")}");
            return Task.FromResult(new BridgeCommandResult(command.Id, command.Name, true));
        }
    }

    private sealed class FakeTransport : IBridgeTransport
    {
        private readonly Channel<string> _published = Channel.CreateUnbounded<string>();

        public Channel<BridgeCommand> Commands { get; } = Channel.CreateUnbounded<BridgeCommand>();

        public bool FailStates { get; init; }

        public async Task<List<string>> TakeAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = new List<string>();
            while (result.Count < count)
                result.Add(await _published.Reader.ReadAsync(timeout.Token));
            return result;
        }

        public Task PublishLoginAsync(LoginState state, CancellationToken ct) => Publish($"login {state.Status}");

        public Task PublishStateAsync(StationState state, CancellationToken ct) =>
            FailStates ? throw new IOException("broker down") : Publish($"state {state.StationSn}");

        public Task PublishEventAsync(BridgeEvent bridgeEvent, CancellationToken ct) => Publish($"event {bridgeEvent.Event.GetType().Name}");

        public Task PublishResultAsync(BridgeCommandResult result, CancellationToken ct) => Publish($"result {result.Id} {result.Name}");

        public async IAsyncEnumerable<BridgeCommand> ReadCommandsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var command in Commands.Reader.ReadAllAsync(ct))
                yield return command;
        }

        private Task Publish(string text)
        {
            _published.Writer.TryWrite(text);
            return Task.CompletedTask;
        }
    }
}

public class ConsoleCommandParserTests
{
    private static readonly LoginState LoggedIn = new(LoginStatus.Ok);

    [Theory]
    [InlineData("status", BridgeCommands.Status, null)]
    [InlineData("info", BridgeCommands.Info, null)]
    [InlineData("info T8010P0000000001", BridgeCommands.Info, "T8010P0000000001")]
    [InlineData("mode home", BridgeCommands.SetGuardMode, null)]
    [InlineData("MODE away T8010P0000000001", BridgeCommands.SetGuardMode, "T8010P0000000001")]
    public void Commands(string line, string name, string? station)
    {
        var command = ConsoleCommandParser.Parse(line, LoggedIn, "1").Command;
        Assert.Equal(name, command?.Name);
        Assert.Equal(station, command?.StationSn);
    }

    [Fact]
    public void Mode_and_clips_arguments()
    {
        Assert.Equal("home", ConsoleCommandParser.Parse("mode home", LoggedIn, "1").Command?.Arg("mode"));
        Assert.Equal("Front Door", ConsoleCommandParser.Parse("clips Front Door", LoggedIn, "1").Command?.Arg("camera"));
        Assert.Null(ConsoleCommandParser.Parse("clips", LoggedIn, "1").Command?.Arg("camera"));
    }

    [Theory]
    [InlineData("quit")]
    [InlineData("exit")]
    public void Quit(string line) => Assert.True(ConsoleCommandParser.Parse(line, LoggedIn, "1").Quit);

    [Theory]
    [InlineData("bogus")]
    [InlineData("mode")]
    [InlineData("help")]
    public void Help(string line)
    {
        var input = ConsoleCommandParser.Parse(line, LoggedIn, "1");
        Assert.Null(input.Command);
        Assert.Contains("Commands:", input.Help);
    }

    [Fact]
    public void Empty_line_does_nothing() => Assert.Equal(default, ConsoleCommandParser.Parse("  ", LoggedIn, "1"));

    [Fact]
    public void While_a_code_is_wanted_a_bare_line_is_the_code_and_empty_resends()
    {
        var waiting = new LoginState(LoginStatus.NeedVerifyCode);

        var code = ConsoleCommandParser.Parse(" 123456 ", waiting, "1").Command;
        Assert.Equal(BridgeCommands.VerifyCode, code?.Name);
        Assert.Equal("123456", code?.Arg("code"));
        Assert.Equal(BridgeCommands.ResendCode, ConsoleCommandParser.Parse("", new LoginState(LoginStatus.VerifyCodeRejected), "1").Command?.Name);
        Assert.Equal(BridgeCommands.Status, ConsoleCommandParser.Parse("status", waiting, "1").Command?.Name);
    }

    [Fact]
    public void While_a_captcha_is_wanted_a_bare_line_is_the_answer()
    {
        var command = ConsoleCommandParser.Parse("x7Kq", new LoginState(LoginStatus.NeedCaptcha, Captcha: new CaptchaChallenge("cap-1", "")), "1").Command;

        Assert.Equal(BridgeCommands.Captcha, command?.Name);
        Assert.Equal("cap-1", command?.Arg("id"));
        Assert.Equal("x7Kq", command?.Arg("answer"));
    }
}

public class EufyDriverCommandTests
{
    [Fact]
    public async Task Status_without_stations_is_an_empty_list()
    {
        var result = await Driver().ExecuteAsync(new BridgeCommand("1", BridgeCommands.Status), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("[]", result.Data?.GetRawText());
    }

    [Theory]
    [InlineData(BridgeCommands.SetGuardMode)]
    [InlineData(BridgeCommands.Info)]
    [InlineData(BridgeCommands.Clips)]
    public async Task Station_commands_fail_without_a_connected_station(string name)
    {
        var result = await Driver().ExecuteAsync(new BridgeCommand("1", name, Args: new Dictionary<string, string> { ["mode"] = "home" }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("No station is connected", result.Message);
    }

    [Fact]
    public async Task Login_answers_are_refused_when_no_login_waits()
    {
        var result = await Driver().ExecuteAsync(new BridgeCommand("1", BridgeCommands.VerifyCode, Args: new Dictionary<string, string> { ["code"] = "1" }),
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Unknown_command_fails()
    {
        var result = await Driver().ExecuteAsync(new BridgeCommand("7", "reboot"), CancellationToken.None);

        Assert.Equal(("7", false), (result.Id, result.Success));
    }

    private static EufyDriver Driver() =>
        new(new EufyCloudClient(new HttpClient(), new EufyCloudOptions(), new MemorySessionStore()), new EufyDriverOptions());

    private sealed class MemorySessionStore : ISessionStore
    {
        private CloudSessionState? _state;

        public Task<CloudSessionState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_state);

        public Task SaveAsync(CloudSessionState state, CancellationToken cancellationToken = default)
        {
            _state = state;
            return Task.CompletedTask;
        }
    }
}
