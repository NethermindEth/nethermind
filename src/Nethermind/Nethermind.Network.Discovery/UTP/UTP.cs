using System.Collections.Concurrent;
using System.Diagnostics;
using MathNet.Numerics.Random;
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
public class UTPStream(IUTPTransfer peer, ushort connectionId, ILogManager logManager) : IUTPTransfer
{
    private readonly ILogger _logger = logManager.GetClassLogger<UTPStream>();
    private const uint PayloadSize = 508;
    private const int MAX_PAYLOAD_SIZE = 64000;
    private const uint RECEIVE_WINDOW_SIZE = 128000;

    private uint _lastReceivedMicrosecond = 0;
    private UTPPacketHeader? _lastPacketHeaderFromPeer;
    private ushort _seq_nr = 0; // Incremented by ST_SYN and ST_DATA
    private AckInfo _receiverAck_nr = new AckInfo(0, null); // Mutated by receiver only // From spec: c.ack_nr
    private ConnectionState _state;

    // Well... technically I want a deque
    // The head's Header.SeqNumber is analogous to m_acked_seq_nr

    private readonly UTPSynchronizer _utpSynchronizer = new UTPSynchronizer();
    private readonly InflightDataCalculator _inflightDataCalculator = new InflightDataCalculator();
    private readonly LEDBAT _trafficControl = new LEDBAT();

    // TODO: Use LRU instead. Need a special expiry handling so that the _incomingBufferSize is correct.
    private ConcurrentDictionary<ushort, Memory<byte>?> _receiveBuffer = new();
    private ulong _incomingBufferSize = 0;

    public async Task InitiateHandshake(CancellationToken token, ushort? synConnectionId = null)
    {
        // TODO: MTU probe
        // TODO: in libp2p this is added to m_outbuf, and therefore part of the
        // resend logic
        _seq_nr = 1;
        UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StSyn);
        header.ConnectionId = synConnectionId ?? connectionId;
        _seq_nr++;
        _state = ConnectionState.CsSynSent;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await SendSynPacketToReceiver(header, token);
            await _utpSynchronizer.WaitForReceiverToSync();
            if (_state == ConnectionState.CsConnected) break;
        }

        await SendStatePacket(token);
    }

    //Put things in a buffer and signal ReadStram to process those buffer
    public Task ReceiveMessage(UTPPacketHeader packageHeader, ReadOnlySpan<byte> data, CancellationToken token)
    {
        // TODO: Probably should be handled at deserializer level
        if (packageHeader.Version != 1) return Task.CompletedTask;

        // TODO: When EOF was obtained from FIN, filter out sequence higher than that.
        _lastReceivedMicrosecond = packageHeader.TimestampMicros;
        if (_lastPacketHeaderFromPeer == null ||  UTPUtil.IsLessOrEqual(_lastPacketHeaderFromPeer.AckNumber, packageHeader.AckNumber)) {
            _lastPacketHeaderFromPeer = packageHeader;
        }

        switch (packageHeader.PacketType)
        {
            case UTPPacketType.StSyn:
                if (_state == ConnectionState.CsUnInitialized)
                {
                    _seq_nr = (ushort)Random.Shared.Next(); // From spec: c.seq_nr
                    _receiverAck_nr = new AckInfo(packageHeader.SeqNumber, null); // From spec: c.ack_nr
                    _state = ConnectionState.CsSynRecv; // From spec: c.state
                    _utpSynchronizer.AwakeReceiverToStarSynchronization(packageHeader); //must start ReadStream loop
                }
                break;

            case UTPPacketType.StState:
                if (_state == ConnectionState.CsSynSent)
                {
                    // The seqNumber need to Subtract 1.
                    // This is because the first StData would have the same sequence number
                    // And we would skip it if we don't subtract 1.
                    // Returning this ack seems to be fine.
                    _receiverAck_nr = new AckInfo((ushort)(packageHeader.SeqNumber - 1), null); // From spec: c.ack_nr
                    _state = ConnectionState.CsConnected;
                }
                break;

            case UTPPacketType.StFin:
                if (!ShouldNotHandleSequence(packageHeader, ReadOnlySpan<byte>.Empty))
                {
                    _receiveBuffer[packageHeader.SeqNumber] = null;
                }

                if (_state == ConnectionState.CsEnded)
                {
                    // Special case, once FIN is first received, the reader stream thread would have exited after its ACK.
                    // In the case where the last ACK is lost, the sender send FIN again, so this need to happen.
                    //_ = SendStatePacket(token);
                    UTPPacketHeader stateHeader = CreateBaseHeader(UTPPacketType.StState);
                    _ = peer.ReceiveMessage(stateHeader, ReadOnlySpan<byte>.Empty, token);
                }

                break;

            case UTPPacketType.StData:
                if (_state == ConnectionState.CsSynRecv) _state = ConnectionState.CsConnected;
                if (!ShouldNotHandleSequence(packageHeader, data))
                {
                    // TODO: Fast path without going to receive buffer?
                    _incomingBufferSize += (ulong)data.Length;
                    _receiveBuffer[packageHeader.SeqNumber] = data.ToArray();
                }

                break;

            case UTPPacketType.StReset:
                _state = ConnectionState.CsEnded;
                break;
        }

        _utpSynchronizer.awakePeer();
        // TODO: step every minute, apparently
        /**
                 * 	// this is the difference between their send time and our receive time
                    // 0 means no sample yet
                    std::uint32_t their_delay = 0;
                    if (ph->timestamp_microseconds != 0)
                    {
                        std::uint32_t timestamp = std::uint32_t(total_microseconds(
                            receive_time.time_since_epoch()) & 0xffffffff);
                        m_reply_micro = timestamp - ph->timestamp_microseconds;
                        std::uint32_t const prev_base = m_their_delay_hist.initialized() ? m_their_delay_hist.base() : 0;
                        their_delay = m_their_delay_hist.add_sample(m_reply_micro, step);
                        int const base_change = int(m_their_delay_hist.base() - prev_base);
                        UTP_LOGV("%8p: their_delay::add_sample:%u prev_base:%u new_base:%u\n"
                            , static_cast<void*>(this), m_reply_micro, prev_base, m_their_delay_hist.base());

                        if (prev_base && base_change < 0 && base_change > -10000 && m_delay_hist.initialized())
                        {
                            // their base delay went down. This is caused by clock drift. To compensate,
                            // adjust our base delay upwards
                            // don't adjust more than 10 ms. If the change is that big, something is probably wrong
                            m_delay_hist.adjust_base(-base_change);
                        }

                        UTP_LOGV("%8p: incoming packet reply_micro:%u base_change:%d\n"
                            , static_cast<void*>(this), m_reply_micro, prev_base ? base_change : 0);
                    }
                    // the test for INT_MAX here is a work-around for a bug in uTorrent where
                        // it's sometimes sent as INT_MAX when it is in fact uninitialized
                        const std::uint32_t sample = ph->timestamp_difference_microseconds == INT_MAX
                            ? 0 : ph->timestamp_difference_microseconds;

                        std::uint32_t delay = 0;
                        if (sample != 0)
                        {
                            delay = m_delay_hist.add_sample(sample, step);
                            m_delay_sample_hist[m_delay_sample_idx++] = delay;
                            if (m_delay_sample_idx >= m_delay_sample_hist.size())
                                m_delay_sample_idx = 0;
                        }
                 */

        return Task.CompletedTask;
    }

    public async Task HandleHandshake(CancellationToken token)
    {
        await _utpSynchronizer.WaitTillSenderSendsST_SYNAndReceiverReceiveIt();
        await SendStatePacket(token);
    }

    public async Task ReadStream(Stream output, CancellationToken token)
    {
        bool finished = false;
        while (!finished && _state != ConnectionState.CsEnded)
        {
            token.ThrowIfCancellationRequested();

            ushort curAck = _receiverAck_nr.seq_nr;
            long packetIngested = 0;

            while (_receiveBuffer.TryRemove(UTPUtil.WrappedAddOne(curAck), out Memory<byte>? packetData))
            {
                curAck++;

                if (packetData == null)
                {
                    // Its unclear if this is a bidirectional stream should the sending get aborted also or not.
                    finished = true;
                    output.Close();
                    break;
                }

                // Periodically send out ack.
                packetIngested++;
                if (packetIngested > 4)
                {
                    _receiverAck_nr = new AckInfo(curAck, null);
                    await SendStatePacket(token);
                    packetIngested = 0;
                }

                await output.WriteAsync(packetData.Value, token);
                _incomingBufferSize -= (ulong)packetData.Value.Length;
            }

            // Assembling ack.
            byte[]? selectiveAck = null;
            if (_receiveBuffer.Count >= 2) // If its only one, then the logic on the receiver is kinda useless
            {
                selectiveAck = UTPUtil.CompileSelectiveAckBitset(curAck, _receiveBuffer);
            }

            _receiverAck_nr = new AckInfo(curAck, selectiveAck);

            await SendStatePacket(token);

            await _utpSynchronizer.WaitForReceiverToSync();
        }
    }

    public async Task WriteStream(Stream input, CancellationToken token)
    {
        bool streamFinished = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (_lastPacketHeaderFromPeer != null)
            {
                ulong initialWindowSize = _inflightDataCalculator.GetCurrentUInflightData();
                _inflightDataCalculator.CalculateInflightData(_lastPacketHeaderFromPeer);
                ulong ackedBytes = initialWindowSize - _inflightDataCalculator.GetCurrentUInflightData();
                _trafficControl.OnAck(ackedBytes, initialWindowSize, _lastPacketHeaderFromPeer.TimestampDeltaMicros, UTPUtil.GetTimestamp());

            }

            await Retransmit(_inflightDataCalculator.getUnAckedWindow(), token);

            while (!streamFinished && isSpaceAvailableOnStream())
            {
                byte[] buffer = new byte[PayloadSize];
                int readLength = await input.ReadAsync(buffer, token);

                if (readLength != 0) {  // Note: We assume ReadAsync will return 0 multiple time.
                    await SendPacket(UTPPacketType.StData, buffer.AsMemory()[..readLength], token);
                    _seq_nr++;
                }else {
                    await SendPacket(UTPPacketType.StFin, Memory<byte>.Empty, token);
                    streamFinished = true;
                }
            }
            if (streamFinished && _inflightDataCalculator.isUnackedWindowEmpty()) break;
            await _utpSynchronizer.WaitForReceiverToSync();
        }
    }

    private bool isSpaceAvailableOnStream()
    {
        return _inflightDataCalculator.GetCurrentInflightData() + PayloadSize < _trafficControl.WindowSize;
    }

    private async Task SendPacket(UTPPacketType type, Memory<byte> asMemory, CancellationToken token)
    {
        UTPPacketHeader header = CreateBaseHeader(type);
        _inflightDataCalculator.trackPacket(asMemory, header);
        await peer.ReceiveMessage(header, asMemory.Span, token);
        _inflightDataCalculator.IncrementInflightData(asMemory.Length);
    }

    private async Task SendStatePacket(CancellationToken token)
    {
        UTPPacketHeader stateHeader = CreateBaseHeader(UTPPacketType.StState);
        await peer.ReceiveMessage(stateHeader, ReadOnlySpan<byte>.Empty, token);
    }
    private bool ShouldNotHandleSequence(UTPPacketHeader meta, ReadOnlySpan<byte> data)
    {
        ushort receiverAck = _receiverAck_nr.seq_nr;

        if (data.Length > MAX_PAYLOAD_SIZE)
        {
            return true;
        }

        // Got previous resend data probably
        if (UTPUtil.IsLess(meta.SeqNumber, receiverAck))
        {
            if (_logger.IsTrace) _logger.Trace($"less than ack {meta.SeqNumber}");
            return true;
        }

        // What if the sequence number is way too high. The threshold is estimated based on the window size
        // and the payload size
        if (!UTPUtil.IsLess(meta.SeqNumber, (ushort)(receiverAck + RECEIVE_WINDOW_SIZE / PayloadSize)))
        {
            return true;
        }

        // No space in buffer UNLESS its the next needed sequence, in which case, we still process it, otherwise
        // it can get stuck.
        if (_incomingBufferSize + (ulong)data.Length > RECEIVE_WINDOW_SIZE &&
            meta.SeqNumber != UTPUtil.WrappedAddOne(receiverAck))
        {
            // Attempt to clean incoming buffer.
            // Ideally, it just use an LRU instead.
            List<ushort> keptBuffers = _receiveBuffer.Select(kv => kv.Key).ToList();
            foreach (var keptBuffer in keptBuffers)
            {
                // Can happen sometime, the buffer is written at the same time as it is being processed so the ack
                // is not updated yet at that point.
                if (UTPUtil.IsLess(keptBuffer, receiverAck))
                {
                    if (_receiveBuffer.TryRemove(keptBuffer, out Memory<byte>? mem))
                    {
                        if (mem != null) _incomingBufferSize -= (ulong)mem.Value.Length;
                    }
                }
            }

            if (_incomingBufferSize + (ulong)data.Length > RECEIVE_WINDOW_SIZE)
            {
                // Sort them by its distance from receiverAck in descending order
                keptBuffers.Sort((it1, it2) => (it2 - receiverAck) - (it1 - receiverAck));

                ulong targetSize = _incomingBufferSize / 2;
                ushort minKept = (ushort)(_seq_nr + 65);
                int i = 0;
                while (i < keptBuffers.Count && _incomingBufferSize > targetSize)
                {
                    if (UTPUtil.IsLess(keptBuffers[i], minKept))
                        continue; // Must keep at least 65 items as we could ack them via selective ack
                    if (_receiveBuffer.TryRemove(keptBuffers[i], out Memory<byte>? mem))
                    {
                        if (mem != null) _incomingBufferSize -= (ulong)mem.Value.Length;
                    }

                    i++;
                }
            }

            return true;
        }

        if (_logger.IsTrace) _logger.Trace($"ok data {meta.SeqNumber}");
        return false;
    }

    private async Task SendSynPacketToReceiver(UTPPacketHeader header, CancellationToken token)
    {
        await peer.ReceiveMessage(header, ReadOnlySpan<byte>.Empty, token);
    }

    private async Task Retransmit(LinkedList<UnackedItem> unackedWindow, CancellationToken token)
    {
        UTPPacketHeader? lastPacketFromPeer = _lastPacketHeaderFromPeer;
        if (lastPacketFromPeer == null)
        {
            return;
        }

        var nextUnackedEntry = unackedWindow.First;
        if (nextUnackedEntry == null)
        {
            return;
        }

        // Well... ideally this obtained right during send.
        LinkedListNode<UnackedItem>? curUnackedWindowHead = nextUnackedEntry;
        if (UTPUtil.IsLessOrEqual(curUnackedWindowHead.Value.Header.SeqNumber, lastPacketFromPeer.AckNumber))
        {
            // Something weird happen here. Could be a new ack that just come in at point curUnackedWindowHead is updated,
            // in which case, just exit, the send loop will re-ingest it again.
            return;
        }

        if (lastPacketFromPeer.SelectiveAck == null)
        {
            UTPPacketHeader nextHeader = nextUnackedEntry.Value.Header;
            ushort ackNumber = lastPacketFromPeer.AckNumber;
            if (UTPUtil.WrappedAddOne(ackNumber) == nextHeader.SeqNumber)
            {
                await MaybeRetransmit(nextUnackedEntry.Value, 1);
            }
        }
        else
        {
            await ProcessSelectiveAck();
        }

        // TODO: In libtorrent there is a logic within tick that retransmit sequence of unacked packet instead checking
        // them one by one.

        async Task ProcessSelectiveAck()
        {
            // TODO: Optimize these
            byte[] selectiveAcks = lastPacketFromPeer.SelectiveAck;
            int totalBits = selectiveAcks.Length * 8;
            int[] ackedAfterCumul = new int[totalBits];

            for (int i = totalBits - 1; i >= 0; i--)
            {
                bool wasAcked = (selectiveAcks[i / 8] & (1 << (i % 8))) > 0;

                if (i == totalBits - 1)
                {
                    ackedAfterCumul[i] = wasAcked ? 1 : 0;
                }
                else
                {
                    // WTF! ACTUAL DYNAMIC PROGRAMMING!
                    ackedAfterCumul[i] = ackedAfterCumul[i + 1] + (wasAcked ? 1 : 0); // Note: Include self.
                }
            }

            // The ackNumber+1 is always unacked when SelectiveAck is set.
            if (ackedAfterCumul[0] >= 3)
            {
                await MaybeRetransmit(curUnackedWindowHead.Value, ackedAfterCumul[0]);
            }

            curUnackedWindowHead = curUnackedWindowHead.Next;
            for (int i = 0; i < totalBits && curUnackedWindowHead != null; i++)
            {
                ushort seqNum = (ushort)(lastPacketFromPeer.AckNumber + 2 + i);

                if (UTPUtil.IsLess(seqNum, curUnackedWindowHead.Value.Header.SeqNumber)) continue;
                Debug.Assert(curUnackedWindowHead.Value.Header.SeqNumber ==
                             seqNum); // It should not be more. Or else something is broken.

                bool wasAcked = (selectiveAcks[i / 8] & (1 << (i % 8))) > 0;

                if (!wasAcked &&
                    ackedAfterCumul[i] >= 3) // Note: include self. But when !wasAcked, it does not add one.
                {
                    if (!await MaybeRetransmit(curUnackedWindowHead.Value, ackedAfterCumul[i]))
                    {
                        // Window limit reached
                        break;
                    }
                }

                curUnackedWindowHead = curUnackedWindowHead.Next;
            }
        }

        async Task<bool> MaybeRetransmit(UnackedItem unackedItem, int unackedCount)
        {
            unackedItem.UnackedCounter += unackedCount;
            if (unackedItem.UnackedCounter < 3)
            {
                // Fast Retransmit.
                // TODO: check this logic https://github.com/arvidn/libtorrent/blob/9aada93c982a2f0f76b129857bec3f885a37c437/include/libtorrent/aux_/utp_stream.hpp#L948
                return true;
            }

            if (!unackedItem.AssumedLoss)
            {
                _inflightDataCalculator.DecrementInflightData(unackedItem.Buffer.Length);
                unackedItem.AssumedLoss = true;
            }

            // TODO: Think again about using a delay to prevent resending ack too early. Think about the inflight data
            // may have just throttle it fine probably.
            // TODO: make overflow safe
            UTPPacketHeader header = unackedItem.Header;
            if (_inflightDataCalculator.GetCurrentInflightData() + unackedItem.Buffer.Length >= _trafficControl.WindowSize)
            {
                return false;
            }

            uint timestamp = UTPUtil.GetTimestamp();
            header.TimestampMicros = timestamp;
            header.TimestampDeltaMicros =
                timestamp - _lastReceivedMicrosecond; // TODO: double check m_reply_micro logic
            header.AckNumber = _receiverAck_nr.seq_nr;
            header.SelectiveAck = _receiverAck_nr.selectiveAckData;

            // Resend it
            _inflightDataCalculator.IncrementInflightData(unackedItem.Buffer.Length);
            unackedItem.AssumedLoss = false;
            unackedItem.UnackedCounter = 0;
            await peer.ReceiveMessage(header, unackedItem.Buffer.Span, token);

            _trafficControl.OnDataLoss(UTPUtil.GetTimestamp());

            return true;
        }
    }

    private UTPPacketHeader CreateBaseHeader(UTPPacketType type)
    {
        uint timestamp = UTPUtil.GetTimestamp();
        return new UTPPacketHeader()
        {
            PacketType = type,
            Version = 1,
            ConnectionId = connectionId,
            // WindowSize = _trafficControl.WindowSize, //Otherwise its really slow
            WindowSize = 1_000_000,
            SeqNumber = _seq_nr,
            AckNumber = _receiverAck_nr.seq_nr,
            SelectiveAck = _receiverAck_nr.selectiveAckData,
            TimestampMicros = timestamp,
            TimestampDeltaMicros = timestamp - _lastReceivedMicrosecond // TODO: double check m_reply_micro logic
        };
    }
}
