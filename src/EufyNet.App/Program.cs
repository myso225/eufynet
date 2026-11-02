using EufyNet.App;
using EufyNet.Cloud;
using EufyNet.Core;
using EufyNet.Transport.Console;
using EufyNet.Transport.Mqtt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

// Eufy bridge: cloud login, P2P to each HomeBase, events and commands through the console and (with Mqtt:Host) MQTT.
//   --force-login   ignore the saved session and log in again
//   --list-only     stop after listing stations and devices
// Ctrl+C / docker stop closes the P2P sessions (END) before exiting.

const string ForceLoginFlag = "--force-login";
const string ListOnlyFlag = "--list-only";

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args.Where(a => a is not ForceLoginFlag and not ListOnlyFlag).ToArray(),
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddUserSecrets<Program>(optional: true);
// Logging:Console:FormatterName picks the format: simple (single line, default) or json (Logging__Console__FormatterName=json).
builder.Logging.ClearProviders().AddConsole();
builder.Services.Configure<SimpleConsoleFormatterOptions>(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Services.Configure<JsonConsoleFormatterOptions>(o => o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz");
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));

var cloudOptions = builder.Configuration.GetSection("Eufy").Get<EufyCloudOptions>() ?? new EufyCloudOptions();
var driverOptions = builder.Configuration.GetSection("Eufy").Get<EufyDriverOptions>() ?? new EufyDriverOptions();
driverOptions.ForceLogin |= args.Contains(ForceLoginFlag);
driverOptions.ListOnly |= args.Contains(ListOnlyFlag);
var dataDir = Path.GetFullPath(driverOptions.DataDir);

builder.Services.AddSingleton(cloudOptions);
builder.Services.AddSingleton(driverOptions);
builder.Services.AddSingleton(new ConsoleTransportOptions { CaptchaFile = Path.Combine(dataDir, "captcha.png") });
builder.Services.AddSingleton(_ => new HttpClient(new EufyHttpHandler(new SocketsHttpHandler())) { Timeout = TimeSpan.FromSeconds(30) });
builder.Services.AddSingleton(sp => new EufyCloudClient(sp.GetRequiredService<HttpClient>(), cloudOptions,
    new JsonFileSessionStore(Path.Combine(dataDir, "cloud-session.json")), sp.GetRequiredService<ILogger<EufyCloudClient>>()));
builder.Services.AddSingleton<EufyDriver>();
builder.Services.AddSingleton<IEufyDriver>(sp => sp.GetRequiredService<EufyDriver>());
builder.Services.AddSingleton<IBridgeTransport, ConsoleTransport>();
// MQTT (Home Assistant discovery, Node-RED events) when Mqtt:Host is set; the console stays for logs and docker attach.
var mqttOptions = builder.Configuration.GetSection("Mqtt").Get<MqttTransportOptions>() ?? new MqttTransportOptions();
if (!string.IsNullOrWhiteSpace(mqttOptions.Host))
{
    builder.Services.AddSingleton(mqttOptions);
    builder.Services.AddSingleton<IBridgeTransport, MqttTransport>();
}
builder.Services.AddHostedService<BridgeService>();

using var host = builder.Build();
if (string.IsNullOrWhiteSpace(cloudOptions.Username) || string.IsNullOrWhiteSpace(cloudOptions.Password))
{
    host.Services.GetRequiredService<ILogger<Program>>()
        .LogError("Set Eufy:Username and Eufy:Password (dotnet user-secrets, or env Eufy__Username / Eufy__Password)");
    return 1;
}

await host.RunAsync();
return Environment.ExitCode;
