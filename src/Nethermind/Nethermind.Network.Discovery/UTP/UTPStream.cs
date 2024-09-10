using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using MathNet.Numerics.Random;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.UTP;

// A UTP stream is the class that translate packets of UTP packet and pipe it into a System.Stream.
// IUTPTransfer is the abstraction that handles the underlying utp packet wrapping and such.
// It is expected that the sender will call WriteStream(Stream, CancellationToken) while the receiver
// will call ReadStream(Stream, CancellationToken).
// Most logic is handled within the WriteStream/ReadStream which makes concurrent states easier to handle
// at the expense of performance and latency, which is probably a bad idea, but at least its easy to make work.
//
// TODO: nagle
// TODO: Parameterized these
// UDP packet that is for sure not going to be fragmented.
// Not sure what to pick for the buffer size.
// TODO: Maybe this can be dynamically adjusted?
// TODO: Ethernet MTU is 1500
public partial class UTPStream(IUTPTransfer peer, ushort connectionId, ILogManager logManager) : IUTPTransfer
{
    private readonly ILogger _logger = logManager.GetClassLogger<UTPStream>();
    private ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;

    private const int PayloadSize = 508;
    private const int MAX_PAYLOAD_SIZE = 64000;
    private readonly uint RECEIVE_WINDOW_SIZE = (uint)500.KiB();
    private const int SELACK_MAX_RESEND = 4;
    private const int DUPLICATE_ACKS_BEFORE_RESEND = 4;

    private uint _lastReceivedMicrosecond = 0;
    private ConnectionState _state = ConnectionState.CsUnInitialized;

    // Put things in a buffer and signal ReadStream to process those buffer
    public Task ReceiveMessage(UTPPacketHeader packetHeader, ReadOnlySpan<byte> data, CancellationToken token)
    {
        // TODO: Probably should be handled at deserializer level
        if (packetHeader.Version != 1) return Task.CompletedTask;

        _lastReceivedMicrosecond = packetHeader.TimestampMicros;

        RecordAck(packetHeader);

        switch (packetHeader.PacketType)
        {
            case UTPPacketType.StSyn:
                _synTcs.TrySetResult(packetHeader);
                break;

            case UTPPacketType.StState:
                // Nullable as its not always that the state is being waited.
                _stateTcs?.TrySetResult(packetHeader);
                break;

            case UTPPacketType.StFin:
                OnStFin(packetHeader, token);

                break;

            case UTPPacketType.StData:
                OnStData(packetHeader, data);

                break;

            case UTPPacketType.StReset:
                _state = ConnectionState.CsEnded;
                break;
        }

        return Task.CompletedTask;
    }

    private UTPPacketHeader CreateBaseHeader(UTPPacketType type)
    {
        var header = new UTPPacketHeader()
        {
            PacketType = type,
            Version = 1,
            ConnectionId = connectionId,
            SeqNumber = _seq_nr,
        };

        RefreshHeader(header);
        return header;
    }

    private void RefreshHeader(UTPPacketHeader header)
    {
        uint timestamp = UTPUtil.GetTimestamp();
        header.TimestampMicros = timestamp;
        header.TimestampDeltaMicros = timestamp - _lastReceivedMicrosecond; // TODO: double check m_reply_micro logic
        header.AckNumber = _receiverAck_nr.seq_nr;
        header.SelectiveAck = _receiverAck_nr.selectiveAckData;
        header.WindowSize = _trafficControl.WindowSize;
    }
}
