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
public class UTPStream(IUTPTransfer peer, ushort connectionId, ILogManager logManager) : IUTPTransfer
{
    private readonly ILogger _logger = logManager.GetClassLogger<UTPStream>();
    private const uint PayloadSize = 508;
    private const int MAX_PAYLOAD_SIZE = 64000;
    private readonly uint RECEIVE_WINDOW_SIZE = (uint)500.KiB();
    private const int SELACK_MAX_RESEND = 4;
    private const int DUPLICATE_ACKS_BEFORE_RESEND = 4;

    private uint _lastReceivedMicrosecond = 0;
    private UTPPacketHeader? _lastPacketHeaderFromPeer;
    private ushort _seq_nr = 0; // Incremented by ST_SYN and ST_DATA
    private AckInfo _receiverAck_nr = new AckInfo(0, null); // Mutated by receiver only // From spec: c.ack_nr
    private ConnectionState _state;

    // Well... technically I want a deque
    // The head's Header.SeqNumber is analogous to m_acked_seq_nr

    private readonly UTPSynchronizer _utpSynchronizer = new UTPSynchronizer();
    private readonly UnackedWindows _unackedWindows = new UnackedWindows(logManager.GetClassLogger<UTPStream>());
    private readonly LEDBAT _trafficControl = new LEDBAT(logManager);

    // TODO: Use LRU instead. Need a special expiry handling so that the _incomingBufferSize is correct.
    private ConcurrentDictionary<ushort, Memory<byte>?> _receiveBuffer = new();
    private int _incomingBufferSize = 0;

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
                    Interlocked.Add(ref _incomingBufferSize, data.Length);
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
            if (_logger.IsTrace) _logger.Trace($"R loop. Available window {_unackedWindows.GetCurrentInflightData() - _trafficControl.WindowSize}");
            token.ThrowIfCancellationRequested();

            ushort curAck = _receiverAck_nr.seq_nr;
            long packetIngested = 0;

            while (_receiveBuffer.TryRemove(UTPUtil.WrappedAddOne(curAck), out Memory<byte>? packetData))
            {
                curAck++;
                if (_logger.IsTrace) _logger.Trace($"R ingest {curAck}");

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
                Interlocked.Add(ref _incomingBufferSize, -packetData.Value.Length);
            }

            // Assembling ack.
            byte[]? selectiveAck = UTPUtil.CompileSelectiveAckBitset(curAck, _receiveBuffer);

            _receiverAck_nr = new AckInfo(curAck, selectiveAck);
            if (_logger.IsTrace) _logger.Trace($"R ack set to {curAck}. Bufsixe is {_receiveBuffer.Count()}");

            await SendStatePacket(token);

            if (_logger.IsTrace) _logger.Trace($"R wait");
            await _utpSynchronizer.WaitForReceiverToSync(100);
        }
    }

    public async Task WriteStream(Stream input, CancellationToken token)
    {
        bool streamFinished = false;
        while (true)
        {
            if (_logger.IsTrace) _logger.Trace("S loop start");
            token.ThrowIfCancellationRequested();

            uint now = UTPUtil.GetTimestamp();
            if (_lastPacketHeaderFromPeer != null)
            {
                ulong initialWindowSize = _unackedWindows.GetCurrentUInflightData();
                ulong ackedBytes = (ulong) _unackedWindows.ProcessAck(_lastPacketHeaderFromPeer, now);
                _trafficControl.OnAck(ackedBytes, initialWindowSize, _lastPacketHeaderFromPeer.TimestampDeltaMicros, UTPUtil.GetTimestamp());
            }

            if (_unackedWindows.rtoTimeout > 0 && now > _unackedWindows.rtoTimeout)
            {
                if (_logger.IsTrace) _logger.Trace($"S rto timeout");
                _unackedWindows.OnRtoTimeout();
                // Retransmit everything
            }
            else
            {
                Retransmit(_unackedWindows.getUnAckedWindow());
            }

            if (_logger.IsTrace) _logger.Trace($"S Space available {isSpaceAvailableOnStream()}, {_unackedWindows.GetCurrentInflightData()}, {_trafficControl.WindowSize} F {streamFinished}");
            while (!streamFinished && isSpaceAvailableOnStream())
            {
                if (_logger.IsTrace) _logger.Trace($"S send {_seq_nr}");
                byte[] buffer = new byte[PayloadSize];
                int readLength = await input.ReadAsync(buffer, token);

                if (readLength != 0) {  // Note: We assume ReadAsync will return 0 multiple time.
                    UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StData);
                    _unackedWindows.trackPacket(buffer.AsMemory()[..readLength], header, UTPUtil.GetTimestamp());
                    _seq_nr++;
                }else {
                    UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StFin);
                    _unackedWindows.trackPacket(Memory<byte>.Empty, header, UTPUtil.GetTimestamp());
                    streamFinished = true;
                }
            }

            await FlushPackets(token);
            if (streamFinished && _unackedWindows.isUnackedWindowEmpty()) break;
            await _utpSynchronizer.WaitForReceiverToSync();
        }
    }

    private async Task FlushPackets(CancellationToken token)
    {
        var linkedListNode = _unackedWindows.getUnAckedWindow().First;
        while (linkedListNode != null)
        {
            var entry = linkedListNode.Value;
            if (entry.NeedResent || entry.TransmitCount == 0)
            {
                entry.TransmitCount++;

                var header = entry.Header;
                uint timestamp = UTPUtil.GetTimestamp();
                header.TimestampMicros = timestamp;
                header.TimestampDeltaMicros = timestamp - _lastReceivedMicrosecond; // TODO: double check m_reply_micro logic
                header.AckNumber = _receiverAck_nr.seq_nr;
                header.SelectiveAck = _receiverAck_nr.selectiveAckData;
                await peer.ReceiveMessage(header, entry.Buffer.Span, token);
            }
            linkedListNode = linkedListNode.Next;
        }
    }

    private bool isSpaceAvailableOnStream()
    {
        return _unackedWindows.GetCurrentInflightData() + PayloadSize < _trafficControl.WindowSize;
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

        if (_incomingBufferSize + data.Length > RECEIVE_WINDOW_SIZE &&
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
                        if (mem != null) _incomingBufferSize -= mem.Value.Length;
                    }
                }
            }

            if (_incomingBufferSize + data.Length > RECEIVE_WINDOW_SIZE)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Receive buffer full for seq {meta.SeqNumber}");
                    var it = _receiveBuffer.Select(kv => kv.Key).ToList();
                    _logger.Trace("The nums " + string.Join(", ", it));
                }

                return true;
            }
        }

        return false;
    }

    private async Task SendSynPacketToReceiver(UTPPacketHeader header, CancellationToken token)
    {
        await peer.ReceiveMessage(header, ReadOnlySpan<byte>.Empty, token);
    }

    private void Retransmit(LinkedList<UnackedItem> unackedWindow)
    {
        if (_logger.IsTrace) _logger.Trace($"S Retransmit");
        UTPPacketHeader? lastPacketFromPeer = _lastPacketHeaderFromPeer;
        if (lastPacketFromPeer == null)
        {
            return;
        }


        var nextUnackedEntry = unackedWindow.First;
        if (nextUnackedEntry == null)
        {
            if (_logger.IsTrace) _logger.Trace($"S No unacked window");
            return;
        }

        // Well... ideally this obtained right during send.
        LinkedListNode<UnackedItem>? curUnackedWindowHead = nextUnackedEntry;
        if (UTPUtil.IsLessOrEqual(curUnackedWindowHead.Value.Header.SeqNumber, lastPacketFromPeer.AckNumber))
        {
            // Something weird happen here. Could be a new ack that just come in at point curUnackedWindowHead is updated,
            // in which case, just exit, the send loop will re-ingest it again.
            if (_logger.IsTrace) _logger.Trace($"S strange case {curUnackedWindowHead.Value.Header.SeqNumber} {lastPacketFromPeer.AckNumber}");
            return;
        }

        int retransmitCount = 0;

        if (lastPacketFromPeer.SelectiveAck == null)
        {
            UTPPacketHeader nextHeader = nextUnackedEntry.Value.Header;
            ushort ackNumber = lastPacketFromPeer.AckNumber;
            if (UTPUtil.WrappedAddOne(ackNumber) == nextHeader.SeqNumber)
            {
                MaybeRetransmit(nextUnackedEntry.Value, 1);
            }
        }
        else
        {
            ProcessSelectiveAck();
        }

        // TODO: In libtorrent there is a logic within tick that retransmit sequence of unacked packet instead checking
        // them one by one.
        void ProcessSelectiveAck()
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
            // 0 here then refers to ack+2
            if (UTPUtil.IsLess((ushort)(lastPacketFromPeer.AckNumber + 1), curUnackedWindowHead.Value.Header.SeqNumber)) throw new Exception("haha! I mess up!");
            if (ackedAfterCumul[0] >= 3)
            {
                MaybeRetransmit(curUnackedWindowHead.Value, ackedAfterCumul[0]);
            }

            curUnackedWindowHead = curUnackedWindowHead.Next;
            for (int i = 0; i < totalBits && curUnackedWindowHead != null && retransmitCount < SELACK_MAX_RESEND; i++)
            {
                ushort seqNum = (ushort)(lastPacketFromPeer.AckNumber + 2 + i);

                if (UTPUtil.IsLess(seqNum, curUnackedWindowHead.Value.Header.SeqNumber)) continue;

                Debug.Assert(curUnackedWindowHead.Value.Header.SeqNumber == seqNum);

                bool wasAcked = (selectiveAcks[i / 8] & (1 << (i % 8))) > 0;
                if (!wasAcked && ackedAfterCumul[i] >= DUPLICATE_ACKS_BEFORE_RESEND) // Note: include self. But when !wasAcked, it does not add one.
                {
                    MaybeRetransmit(curUnackedWindowHead.Value, ackedAfterCumul[i]);
                }

                curUnackedWindowHead = curUnackedWindowHead.Next;
            }
        }

        void MaybeRetransmit(UnackedItem unackedItem, int unackedCount)
        {
            var result = _unackedWindows.OnUnack(unackedItem, unackedCount, _trafficControl.WindowSize);
            if (result == UnackedWindows.OnUnackResult.No)
            {
                return;
            }

            if (result == UnackedWindows.OnUnackResult.WindowFull)
            {
                return;
            }

            // Retransmit
            UTPPacketHeader header = unackedItem.Header;
            if (_logger.IsTrace) _logger.Trace($"S Retransmit {header.SeqNumber}");
            _trafficControl.OnDataLoss(UTPUtil.GetTimestamp());
            unackedItem.NeedResent = true;
            retransmitCount++;
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
            WindowSize = _trafficControl.WindowSize, //Otherwise its really slow
            SeqNumber = _seq_nr,
            AckNumber = _receiverAck_nr.seq_nr,
            SelectiveAck = _receiverAck_nr.selectiveAckData,
            TimestampMicros = timestamp,
            TimestampDeltaMicros = timestamp - _lastReceivedMicrosecond // TODO: double check m_reply_micro logic
        };
    }
}
