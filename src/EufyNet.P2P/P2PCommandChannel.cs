using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EufyNet.P2P;

public sealed record P2PCommandResult(CommandType Command, int ReturnCode, byte[] Data)
{
    public bool IsSuccess => ReturnCode == (int)P2PErrorCode.ErrorPpcsSuccessful;

    public string ReturnCodeName => Enum.IsDefined(typeof(P2PErrorCode), ReturnCode) ? ((P2PErrorCode)ReturnCode).ToString() : ReturnCode.ToString();
}

public sealed class P2PCommandTimeoutException(CommandType command, string stage)
    : TimeoutException($"{command}: no {stage} from the station")
{
    public CommandType Command { get; } = command;
    /// <summary>"ACK" (station never confirmed the packet) or "result".</summary>
    public string Stage { get; } = stage;
}

public sealed class P2PCommandChannelOptions
{
    public TimeSpan ResendInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ResultTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>How often a command answered with ERROR_FAILED_TO_REQUEST is sent again.</summary>
    public int FailedToRequestRetries { get; set; } = 2;
}

/// <summary>
/// Sends commands over a connected <see cref="P2PSession"/> one at a time: resend until ACKed, then wait for the
/// result with the same command id. Reassembles incoming messages and decrypts results and control messages.
/// Covers the command part of eufy-security-client p2p/session.js (sendCommand / _sendCommand / handleData).
/// </summary>
public sealed class P2PCommandChannel : IDisposable
{
    private readonly P2PSession _session;
    private readonly P2PCommandChannelOptions _options;
    private readonly ILogger _logger;
    private readonly P2PMessageAssembler _assembler = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _level1Key;

    private volatile Pending? _pending;
    private ushort _sequence;
    private byte[]? _key;

    public P2PCommandChannel(P2PSession session, string stationSn, string p2pDid, P2PCommandChannelOptions? options = null, ILogger<P2PCommandChannel>? logger = null)
    {
        _session = session;
        _options = options ?? new P2PCommandChannelOptions();
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _level1Key = P2PCrypto.Level1Key(stationSn, p2pDid);
        _session.DataReceived += OnData;
        _session.AckReceived += OnAck;
    }

    public EncryptionLevel Encryption { get; private set; } = EncryptionLevel.None;

    public byte[] Level1Key => _level1Key;

    /// <summary>Unsolicited CONTROL-channel messages (events, gateway info), decrypted. Raised on the receive loop.</summary>
    public event Action<P2PMessage>? ControlMessage;

    /// <summary>DATA-channel messages that are not the result of the command in flight.</summary>
    public event Action<P2PMessage>? UnmatchedMessage;

    public void SetEncryption(EncryptionLevel level, byte[]? key)
    {
        Encryption = level;
        _key = level == EncryptionLevel.Level1 ? _level1Key : key;
    }

    public async Task<P2PCommandResult> SendAsync(P2PCommand command, TimeSpan? resultTimeout = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var result = await SendOnceAsync(command, resultTimeout ?? _options.ResultTimeout, cancellationToken);
                if (result.ReturnCode == (int)P2PErrorCode.ErrorFailedToRequest && attempt < _options.FailedToRequestRetries)
                {
                    _logger.LogDebug("{Command} answered ERROR_FAILED_TO_REQUEST, sending again", command.Description);
                    continue;
                }
                return result;
            }
        }
        finally
        {
            _pending = null;
            _gate.Release();
        }
    }

    /// <summary>Completes the command in flight when its answer arrives some other way (e.g. CMD_GATEWAYINFO on CONTROL).</summary>
    public bool TryCompletePending(P2PCommandResult result)
    {
        if (_pending is not { } pending || pending.Command != result.Command || !pending.Result.TrySetResult(result))
            return false;
        pending.Acked.TrySetResult();
        return true;
    }

    public void Dispose()
    {
        _session.DataReceived -= OnData;
        _session.AckReceived -= OnAck;
        _pending?.Result.TrySetCanceled();
        _gate.Dispose();
    }

    private async Task<P2PCommandResult> SendOnceAsync(P2PCommand command, TimeSpan resultTimeout, CancellationToken ct)
    {
        var sequence = _sequence++;
        var packet = P2PCommandBuilder.CommandPacket(sequence, command.Type, command.BuildPayload(Encryption, _key));
        var pending = new Pending(sequence, command.Type);
        _pending = pending;
        _logger.LogDebug("Sending {Command} seq {Sequence} ({Encryption})", command.Description, sequence, Encryption);

        var ackDeadline = DateTime.UtcNow + _options.AckTimeout;
        while (!pending.Acked.Task.IsCompleted)
        {
            if (DateTime.UtcNow > ackDeadline)
                throw new P2PCommandTimeoutException(command.Type, "ACK");
            await _session.SendAsync(packet, ct);
            await Task.WhenAny(pending.Acked.Task, Task.Delay(_options.ResendInterval, ct));
            ct.ThrowIfCancellationRequested();
        }

        try
        {
            var result = await pending.Result.Task.WaitAsync(resultTimeout, ct);
            _logger.LogDebug("{Command} -> {ReturnCode} ({Length} bytes)", command.Type, result.ReturnCodeName, result.Data.Length);
            return result;
        }
        catch (TimeoutException)
        {
            throw new P2PCommandTimeoutException(command.Type, "result");
        }
    }

    private void OnAck(P2PDataType dataType, ushort[] sequences)
    {
        if (dataType == P2PDataType.Data && _pending is { } pending && sequences.Contains(pending.Sequence))
            pending.Acked.TrySetResult();
    }

    private void OnData(P2PDataPacket packet)
    {
        // Video/binary streams (live view, downloads) are not handled yet.
        if (packet.DataType is not (P2PDataType.Data or P2PDataType.Control))
            return;

        foreach (var message in _assembler.Add(packet))
        {
            try
            {
                if (message.DataType == P2PDataType.Control)
                    ControlMessage?.Invoke(DecryptIfSigned(message, _level1Key));
                else
                    HandleDataMessage(message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Handling {DataType} {Command} failed", message.DataType, message.CommandId);
            }
        }
    }

    private void HandleDataMessage(P2PMessage message)
    {
        if (!message.IsResult)
        {
            UnmatchedMessage?.Invoke(message);
            return;
        }

        var decrypted = _key is null ? message : DecryptIfSigned(message, _key);
        var data = decrypted.Data;
        var returnCode = data.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(data) : 0;
        var result = new P2PCommandResult(message.CommandId, returnCode, data.Length > 4 ? data[4..] : []);

        if (_pending is { } pending && pending.Command == message.CommandId && pending.Result.TrySetResult(result))
        {
            pending.Acked.TrySetResult(); // a result implies the station got the command, even if its ACK was lost
            return;
        }

        _logger.LogDebug("Unmatched result {Command} -> {ReturnCode}", message.CommandId, result.ReturnCodeName);
        UnmatchedMessage?.Invoke(decrypted);
    }

    /// <summary>Messages with signCode &gt; 0 carry an AES-128-ECB body (when it is a whole number of blocks).</summary>
    private static P2PMessage DecryptIfSigned(P2PMessage message, byte[] key)
    {
        if (message.SignCode == 0 || message.Data.Length == 0 || message.Data.Length % 16 != 0)
            return message;
        return message with { Data = P2PCrypto.DecryptEcb(message.Data, key) };
    }

    private sealed class Pending(ushort sequence, CommandType command)
    {
        public ushort Sequence { get; } = sequence;
        public CommandType Command { get; } = command;
        public TaskCompletionSource Acked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<P2PCommandResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
