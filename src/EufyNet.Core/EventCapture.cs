using System.Text.Json;
using EufyNet.P2P;

namespace EufyNet.Core;

/// <summary>Appends every station message (except pings) as one JSON line. Raw material for new event parsers.</summary>
public sealed class EventCapture(string path)
{
    private readonly Lock _lock = new();

    public void Write(string stationSn, P2PMessage message)
    {
        if (message.CommandId == CommandType.CmdPing)
            return;
        var line = JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.Now,
            station = stationSn,
            dataType = message.DataType.ToString(),
            command = message.CommandId.ToString(),
            commandId = (int)message.CommandId,
            channel = message.Channel,
            signCode = message.SignCode,
            type = message.Type,
            payload = HomeBaseEventParser.PayloadText(message.Data),
            hex = Convert.ToHexStringLower(message.Data),
        });
        lock (_lock)
            File.AppendAllText(path, line + Environment.NewLine);
    }
}
