using System.Collections.Concurrent;
using System.Diagnostics;

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
public class UTPStream(IUTPTransfer peer, ushort connectionId) : IUTPTransfer
{
    private const uint PAYLOAD_SIZE = 508;
    private const int MAX_PAYLOAD_SIZE = 64000;
    private const uint RECEIVE_WINDOW_SIZE = 128000;

    private uint _lastReceivedMicrosecond = 0;
    private UTPPacketHeader? _lastPacketHeaderFromPeer;
    private ushort _seq_nr = 0; // Incremented by ST_SYN and ST_DATA
    private AckInfo _receiverAck_nr = new AckInfo(0, null); // Mutated by receiver only // From spec: c.ack_nr
    private ConnectionState _state;

    // Well... technically I want a deque
    // The head's Header.SeqNumber is analogous to m_acked_seq_nr
    private LinkedList<UnackedItem> unackedWindow = new LinkedList<UnackedItem>();
    private readonly UTPSynchronizer _utpSynchronizer = new UTPSynchronizer();
    private readonly InflightDataCalculator _inflightDataCalculator = new InflightDataCalculator();
    private readonly LedBat _trafficControl = new LedBat(UTPUtil.GetTimestamp());

    // TODO: Use LRU instead. Need a special expiry handling so that the _incomingBufferSize is correct.
    private ConcurrentDictionary<ushort, Memory<byte>?> _receiveBuffer = new();
    private ulong _incomingBufferSize = 0;

    //Put things in a buffer and signal ReadStram to process those buffer
    public Task ReceiveMessage(UTPPacketHeader packageHeader, ReadOnlySpan<byte> data, CancellationToken token)
    {
        // TODO: Probably should be handled at deserializer level
        if (packageHeader.Version != 1) return Task.CompletedTask;

        // TODO: When EOF was obtained from FIN, filter out sequence higher than that.
        _lastReceivedMicrosecond = packageHeader.TimestampMicros;
        _lastPacketHeaderFromPeer = UpdateLastUTPPackageHeaderReceived(packageHeader);

        switch (packageHeader.PacketType)
        {
            case UTPPacketType.StSyn:
                _seq_nr = (ushort)Random.Shared.Next(); // From spec: c.seq_nr
                _receiverAck_nr = new AckInfo(packageHeader.SeqNumber, null); // From spec: c.ack_nr
                _state = ConnectionState.CsSynRecv; // From spec: c.state
                _utpSynchronizer.AwakeReceiverToStarSynchronization(packageHeader); //must start ReadStream loop
                break;

            case UTPPacketType.StState:
                if (_state == ConnectionState.CsSynSent) _state = ConnectionState.CsConnected;
                // Otherwise most logic is in ProcessAck.
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
                    _ = SendStatePackage(token);
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

    public async Task ReadStream(Stream output, CancellationToken token)
    {
        await _utpSynchronizer.WaitTillSenderSendsST_SYNAndReceiverReceiveIt();
        bool finished = false;
        while (!finished || _state == ConnectionState.CsEnded)
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
                    await SendStatePackage(token);
                    packetIngested = 0;
                }

                Console.Error.WriteLine($"R ingest {curAck}");
                await output.WriteAsync(packetData.Value, token);
                _incomingBufferSize -= (ulong)packetData.Value.Length;
            }

            // Assembling ack.
            byte[]? selectiveAck = null;
            if (_receiveBuffer.Count >= 2) // If its only one, then the logic on the receiver is kinda useless
            {
                selectiveAck = CompileSelectiveAckBitset(curAck, _receiveBuffer);
            }

            Console.Error.WriteLine($"R set receiver ack {curAck}");
            _receiverAck_nr = new AckInfo(curAck, selectiveAck);

            await SendStatePackage(token);
            await _utpSynchronizer.WaitForReceiverToSync();
        }
    }

    public static byte[] CompileSelectiveAckBitset(ushort curAck,
        ConcurrentDictionary<ushort, Memory<byte>?> receiveBuffer)
    {
        byte[] selectiveAck;
        // Fixed 64 bit.
        // TODO: use long
        // TODO: no need to encode trailing zeros
        selectiveAck = new byte[8];

        // Shortcut the loop if all buffer was iterated
        int counted = 0;
        int maxCounted = receiveBuffer.Count;

        for (int i = 0; i < 64 && counted < maxCounted; i++)
        {
            ushort theAck = (ushort)(curAck + 2 + i);
            if (receiveBuffer.ContainsKey(theAck))
            {
                int iIdx = i / 8;
                int iOffset = i % 8;
                selectiveAck[iIdx] = (byte)(selectiveAck[iIdx] | 1 << iOffset);
                counted++;
            }
        }

        return selectiveAck;
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
                _inflightDataCalculator.CalculateInflightData(_lastPacketHeaderFromPeer, unackedWindow);
                ulong ackedBytes = initialWindowSize - _inflightDataCalculator.GetCurrentUInflightData();
                _trafficControl.OnAck(ackedBytes, initialWindowSize, _lastPacketHeaderFromPeer.TimestampDeltaMicros, UTPUtil.GetTimestamp());
            }

            await Retransmit(unackedWindow, token);
            await IngestStream();

            if (streamFinished && unackedWindow.Count == 0)
            {
                break;
            }

            await _utpSynchronizer.WaitForReceiverToSync();
        }

        async Task IngestStream()
        {
            while (!streamFinished &&
                   _inflightDataCalculator.GetCurrentInflightData() + PAYLOAD_SIZE < _trafficControl.WindowSize)
            {
                byte[] buffer = new byte[PAYLOAD_SIZE];

                int readLength = await input.ReadAsync(buffer, token);
                if (readLength != 0) // Note: We assume ReadAsync will return 0 multiple time.
                {
                    Memory<byte> asMemory = buffer.AsMemory()[..readLength];
                    UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StData);
                    _seq_nr++;
                    Console.Error.WriteLine(
                        $"S Send {header.SeqNumber} {_inflightDataCalculator.GetCurrentInflightData()} {_trafficControl.WindowSize}");
                    unackedWindow.AddLast(new UnackedItem(header, asMemory));
                    await peer.ReceiveMessage(header, asMemory.Span, token);
                    _inflightDataCalculator.IncrementInflightData(asMemory.Length);
                }
                else
                {
                    UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StFin);
                    Memory<byte> asMemory = Memory<byte>.Empty;
                    Console.Error.WriteLine(
                        $"S Send {header.SeqNumber} {_inflightDataCalculator.GetCurrentInflightData()} {_trafficControl.WindowSize}");

                    unackedWindow.AddLast(new UnackedItem(header, asMemory));
                    await peer.ReceiveMessage(header, asMemory.Span, token);
                    _inflightDataCalculator.IncrementInflightData(asMemory.Length);
                    streamFinished = true;
                }
            }
        }
    }

    public async Task InitiateHandshake(CancellationToken token)
    {
        // TODO: MTU probe
        // TODO: in libp2p this is added to m_outbuf, and therefore part of the
        // resend logic
        _seq_nr = 1;
        UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StSyn);
        _seq_nr++;
        _state = ConnectionState.CsSynSent;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await SendSynPackageToReceiver(header, token);
            await _utpSynchronizer.WaitForReceiverToSync();
            if (_state == ConnectionState.CsConnected) break;
        }
    }

    private async Task SendStatePackage(CancellationToken token)
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
            Console.Error.WriteLine($"Ignored due to less than ack {meta.SeqNumber} {receiverAck}");
            return true;
        }

        // What if the sequence number is way too high. The threshold is estimated based on the window size
        // and the payload size
        if (!UTPUtil.IsLess(meta.SeqNumber, (ushort)(receiverAck + RECEIVE_WINDOW_SIZE / PAYLOAD_SIZE)))
        {
            Console.Error.WriteLine(
                $"Ignored due to too high sequnce {meta.SeqNumber} RA {receiverAck} T {(ushort)(receiverAck + RECEIVE_WINDOW_SIZE / PAYLOAD_SIZE)}");
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
                        Console.Error.WriteLine($"Clean {keptBuffer}");
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
                        Console.Error.WriteLine($"Clean {keptBuffers[i]}");
                        if (mem != null) _incomingBufferSize -= (ulong)mem.Value.Length;
                    }

                    i++;
                }
            }

            return true;
        }

        return false;
    }

    private async Task SendSynPackageToReceiver(UTPPacketHeader header, CancellationToken token)
    {
        await peer.ReceiveMessage(header, ReadOnlySpan<byte>.Empty, token);
    }

    private async Task Retransmit(LinkedList<UnackedItem> unackedWindow, CancellationToken token)
    {
        UTPPacketHeader? lastPacketFromPeer = _lastPacketHeaderFromPeer;
        if (lastPacketFromPeer == null)
        {
            Console.Error.WriteLine($"S What?");
            return;
        }

        var nextUnackedEntry = unackedWindow.First;
        if (nextUnackedEntry == null)
        {
            Console.Error.WriteLine($"S Unacked empty");
            // No unacked window
            return;
        }

        // Well... ideally this obtained right during send.
        uint nowMicros = UTPUtil.GetTimestamp();
        LinkedListNode<UnackedItem>? curUnackedWindowHead = nextUnackedEntry;
        if (UTPUtil.IsLessOrEqual(curUnackedWindowHead.Value.Header.SeqNumber, lastPacketFromPeer.AckNumber))
        {
            // Something weird happen here. Could be a new ack that just come in at point curUnackedWindowHead is updated,
            // in which case, just exit, the send loop will re-ingest it again.
            Console.Error.WriteLine(
                $"S Weird caste {curUnackedWindowHead.Value.Header.SeqNumber} {lastPacketFromPeer.AckNumber}");
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

            static string ByteArrayToBitString(byte[] byteArray)
            {
                string bitString = string.Empty;

                foreach (byte b in byteArray)
                {
                    bitString += Convert.ToString(b, 2).PadLeft(8, '0');
                }

                return bitString;
            }

            Console.Error.WriteLine(
                $"S Got selective ack {lastPacketFromPeer.AckNumber} {ByteArrayToBitString(selectiveAcks)}");

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
                        Console.Error.WriteLine($"S Window limit reached in ack");
                        // Window limit reached
                        break;
                    }
                }

                curUnackedWindowHead = curUnackedWindowHead.Next;
            }

            Console.Out.WriteLine($"S sel ack check done");
        }

        async Task<bool> MaybeRetransmit(UnackedItem unackedItem, int unackedCount)
        {
            unackedItem.UnackedCounter += unackedCount;
            if (unackedItem.UnackedCounter < 3)
            {
                // Fast Retransmit.
                Console.Error.WriteLine(
                    $"S not retransmit {unackedItem.Header} because of unacked counter {unackedItem.UnackedCounter}");
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
                Console.Error.WriteLine(
                    $"S Not retransmit {header.SeqNumber} due to window limit {_inflightDataCalculator.GetCurrentInflightData()} {_trafficControl.WindowSize}");
                return false;
            }

            uint timestamp = UTPUtil.GetTimestamp();
            header.TimestampMicros = timestamp;
            header.TimestampDeltaMicros =
                timestamp - _lastReceivedMicrosecond; // TODO: double check m_reply_micro logic
            header.AckNumber = _receiverAck_nr.seq_nr;
            header.SelectiveAck = _receiverAck_nr.selectiveAckData;


            Console.Error.WriteLine($"S Retransmit {header.SeqNumber}");
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
            WindowSize = _trafficControl.WindowSize,
            SeqNumber = _seq_nr,
            AckNumber = _receiverAck_nr.seq_nr,
            SelectiveAck = _receiverAck_nr.selectiveAckData,
            TimestampMicros = timestamp,
            TimestampDeltaMicros = timestamp - _lastReceivedMicrosecond // TODO: double check m_reply_micro logic
        };
    }

    private UTPPacketHeader UpdateLastUTPPackageHeaderReceived(UTPPacketHeader packageHeaderJustReceived)
    {
        if (_lastPacketHeaderFromPeer == null ||
            UTPUtil.IsLessOrEqual(_lastPacketHeaderFromPeer.AckNumber, packageHeaderJustReceived.AckNumber))
        {
            // Note, even if equal, we reset it because selective ack
            return packageHeaderJustReceived;
        }

        return _lastPacketHeaderFromPeer;
    }
}
