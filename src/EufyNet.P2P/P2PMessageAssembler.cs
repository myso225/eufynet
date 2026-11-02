using System.Buffers.Binary;

namespace EufyNet.P2P;

/// <summary>
/// A complete inner message: 16-byte "XZYH" header + body, possibly spread over several DATA packets.
/// <see cref="Type"/> 1 on the DATA channel marks a command result.
/// </summary>
public sealed record P2PMessage(P2PDataType DataType, CommandType CommandId, int Channel, byte SignCode, byte Type, byte[] Data)
{
    public bool IsResult => DataType == P2PDataType.Data && Type == 1;
}

/// <summary>
/// Puts DATA packets back in order per channel (the station resends until ACKed, so duplicates are dropped)
/// and cuts the byte stream into <see cref="P2PMessage"/>s. Replaces parseDataMessage / sortP2PMessageParts.
/// </summary>
public sealed class P2PMessageAssembler
{
    public const int HeaderBytes = 16;
    private const int AlreadyProcessedWindow = 20000;
    private const int MaxQueuedPackets = 512;
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private readonly ChannelState[] _channels = [new(), new(), new(), new()];

    /// <summary>Bytes skipped because they did not start with a header (protocol drift).</summary>
    public long DiscardedBytes { get; private set; }

    public IReadOnlyList<P2PMessage> Add(P2PDataPacket packet)
    {
        var state = _channels[(int)packet.DataType];
        var sequence = packet.Sequence;

        if (sequence == state.Expected)
        {
            state.Append(packet.Payload.Span);
            state.Expected++;
        }
        else if (IsAlreadyProcessed(state.Expected, sequence))
        {
            return [];
        }
        else
        {
            state.Queued.TryAdd(sequence, packet.Payload.ToArray());
            if (state.Queued.Count <= MaxQueuedPackets)
                return [];
            // A packet is lost for good: drop the partial message, skip the gap, resync on the next header.
            state.ClearBuffer();
            state.Expected = state.Queued.Keys.MinBy(k => (ushort)(k - state.Expected));
        }

        while (state.Queued.Remove(state.Expected, out var queued))
        {
            state.Append(queued);
            state.Expected++;
        }
        return Drain(packet.DataType, state);
    }

    /// <summary>True when <paramref name="received"/> lies in the window just before <paramref name="expected"/> (mod 2^16).</summary>
    internal static bool IsAlreadyProcessed(ushort expected, ushort received)
    {
        var behind = (ushort)(expected - received);
        return behind is > 0 and <= AlreadyProcessedWindow;
    }

    private List<P2PMessage> Drain(P2PDataType dataType, ChannelState state)
    {
        var messages = new List<P2PMessage>();
        while (state.Count >= HeaderBytes)
        {
            var buffer = state.Span;
            if (!buffer[..4].SequenceEqual(P2PPackets.MagicWord))
            {
                var next = buffer[1..].IndexOf(P2PPackets.MagicWord);
                var skip = next < 0 ? state.Count - 3 : next + 1;
                DiscardedBytes += skip;
                state.Consume(skip);
                continue;
            }

            var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer[6..10]);
            if (bodyLength > MaxMessageBytes)
            {
                DiscardedBytes += 4;
                state.Consume(4);
                continue;
            }
            if (state.Count < HeaderBytes + bodyLength)
                break;

            messages.Add(new P2PMessage(
                dataType,
                (CommandType)BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..6]),
                buffer[12],
                buffer[13],
                buffer[14],
                buffer.Slice(HeaderBytes, (int)bodyLength).ToArray()));
            state.Consume(HeaderBytes + (int)bodyLength);
        }
        return messages;
    }

    private sealed class ChannelState
    {
        private byte[] _buffer = new byte[4096];

        public ushort Expected { get; set; }
        public Dictionary<ushort, byte[]> Queued { get; } = [];
        public int Count { get; private set; }
        public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Count);

        public void Append(ReadOnlySpan<byte> data)
        {
            if (Count + data.Length > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Count + data.Length));
            data.CopyTo(_buffer.AsSpan(Count));
            Count += data.Length;
        }

        public void Consume(int bytes)
        {
            _buffer.AsSpan(bytes, Count - bytes).CopyTo(_buffer);
            Count -= bytes;
        }

        public void ClearBuffer() => Count = 0;
    }
}
