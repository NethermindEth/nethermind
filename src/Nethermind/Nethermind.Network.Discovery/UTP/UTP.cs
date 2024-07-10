using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.FileIO;

namespace Nethermind.Network.Discovery;

public enum UTPPacketType: byte
{
    StData = 0,
    StFin = 1,
    StState = 2,
    StReset = 3,
    StSyn = 4,
}


enum ConnectionState
{
    CsUnInitialized,
    CsSynSent,
    CsSynRecv,
    CsConnected,
    CsEnded,
}

public interface IUTPTransfer
{
    Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token);
}

// The congestion mechanism of uTP.
// see: https://datatracker.ietf.org/doc/html/rfc6817
// TODO: Parameterized these
// TODO: Double check everything
public class LedBat
{
    // Effectively inf without having to accidentally overflow
    // TODO: Do this properly
    private static int HI = 1_000_000;
    private static uint MAX_WINDOW_SIZE = 1_000_000;

    private static int BASE_HISTORY = 10;
    private static int CURRENT_FILTERS = 100;

    // Screw it, I'll just set a large value as minimum
    private static uint INIT_CWND = 32;
    private static uint MIN_CWND = 32;

    private static uint ALLOWED_INCREASE = 2;
    private static uint GAIN = 1;

    private static uint TARGET = 100_000; // Micros

    // The, RFC mentioned 60 second though, so this should be long lived.
    private static uint BASE_DELAY_ADJ_INTERVAL = 100_000; // Micros.

    // TODO: Dynamically discover these two value
    private static uint MSS = 500;
    private static int RTT = 100_000; // Just an estimate right now

    private FixedRollingAvg CurrentDelays = new FixedRollingAvg(CURRENT_FILTERS, HI, RTT);

    // TODO: The base delay does not need the expiry, AND should be set every BASE_DELAY_ADJ_INTERVAL, if no packet at that time, should be set to HI.
    private FixedRollingAvg BaseDelays = new FixedRollingAvg(BASE_HISTORY, HI, HI);

    private uint _lastBaseDelayAdj = 0;
    private uint _lastDataLossAdjustment = 0;

    public uint WindowSize { get; set; } = INIT_CWND * MSS;

    // Kinda slowstart. See https://github.com/arvidn/libtorrent/blob/9aada93c982a2f0f76b129857bec3f885a37c437/src/utp_stream.cpp#L3223
    private uint SsThres { get; set; }
    private bool IsSlowStart = true;

    public LedBat(uint nowMicros)
    {
    }

    public void OnAck(ulong ackedBytes, ulong flightSize, uint delayMicros, uint nowMicros)
    {
        // TODO: Or.. it can stay uint?
        int delayMicrosInt = (int)delayMicros;

        CurrentDelays.Observe(delayMicrosInt, nowMicros);
        if (_lastBaseDelayAdj / BASE_DELAY_ADJ_INTERVAL != nowMicros / BASE_DELAY_ADJ_INTERVAL)
        {
            _lastBaseDelayAdj = nowMicros;
            BaseDelays.Observe(delayMicrosInt, nowMicros);
        }
        else
        {
            BaseDelays.AdjustMin(delayMicrosInt, nowMicros);
        }

        int delay = CurrentDelays.GetAvgFixed16Precision(nowMicros);
        int baseDelay = BaseDelays.GetAvgFixed16Precision(nowMicros);

        Console.Error.WriteLine($"The delays {delay} {baseDelay}");

        if (delay > baseDelay)
        {
            if (IsSlowStart)
            {
                SsThres = WindowSize / 2;
                IsSlowStart = false;
            }
        }

        bool cwndSaturated = ackedBytes + flightSize + MSS > WindowSize;

        long gain = 0;
        if (cwndSaturated)
        {
            // linear gain
            long queueingDelay = CurrentDelays.GetAvgFixed16Precision(nowMicros) - BaseDelays.GetAvgFixed16Precision(nowMicros);
            long offTarget = ((TARGET << 16) - queueingDelay) / TARGET;
            gain = GAIN * offTarget * (long)ackedBytes * MSS/ (WindowSize << 16);

            long exponentialGain = (long)ackedBytes;

            if (IsSlowStart)
            {
                if (SsThres != 0 && WindowSize + exponentialGain > SsThres)
                {
                    IsSlowStart = false;
                }
                else
                {
                    gain = Math.Max(exponentialGain, gain);
                }
            }
        }
        else
        {
            gain = 0;
        }

        uint newWindow = (uint)(WindowSize + gain);
        uint maxAllowedCwnd = (uint)(flightSize + ALLOWED_INCREASE * MSS);

        newWindow = Math.Min(newWindow, maxAllowedCwnd);
        newWindow = Math.Max(newWindow, MIN_CWND * MSS);
        newWindow = Math.Min(newWindow, MAX_WINDOW_SIZE);

        Console.Error.WriteLine($"TC Window adjusted {WindowSize} -> {newWindow}, {IsSlowStart}, {cwndSaturated}");
        WindowSize = newWindow;

        UpdateCTO();
    }

    private void UpdateCTO()
    {
        // TODO:
        //  implements an RTT estimation mechanism using data
        //  transmission times and ACK reception times,
        //  which is used to implement a congestion timeout (CTO).
    }

    public void OnDataLoss(uint nowMicros)
    {
        Console.Error.WriteLine($"TC Data loss");
        if (_lastDataLossAdjustment / RTT != nowMicros / RTT)
        {
            _lastDataLossAdjustment = nowMicros;
            // TODO: At most once per RTT
            WindowSize = (uint)Math.Max(WindowSize / 2, MIN_CWND * MSS);
        }

        SsThres = WindowSize;
        IsSlowStart = false;
    }

}

public class FixedRollingAvg
{
    private int _size = 0;
    private int _tailIdx = 0;

    private int[] _rollingWindow;
    private uint[] _rollingWindowSetTime;
    private int _sum;
    private readonly int _expiry;
    private readonly int _defaultValue;
    private readonly int _capacity;

    private int HeadIdx => (_tailIdx + _size) % _capacity;

    public FixedRollingAvg(int capacity, int defaultValue, int expiry)
    {
        _rollingWindow = new int[capacity + 1];
        _rollingWindowSetTime = new uint[capacity + 1];
        _defaultValue = defaultValue;
        _expiry = expiry;
        _capacity = capacity;
    }

    public int GetAvgFixed16Precision(uint now)
    {
        while (_size > 0)
        {
            // Not expired. We assume that later observation is always for later time.
            // TODO: make overflow safe
            if (_rollingWindowSetTime[(_tailIdx + 1) % _capacity] + _expiry > now) break;

            _tailIdx++;
            _tailIdx%=_capacity;
            _size--;
            _sum -= _rollingWindow[_tailIdx];
        }

        if (_size == 0) return _defaultValue;
        return (_sum << 16) / _size;
    }

    public int GetAvg(uint now)
    {
        return GetAvgFixed16Precision(now) >> 16;
    }

    public void Observe(int delay, uint now)
    {
        if (_size == _capacity) // Full
        {
            _tailIdx++;
            _tailIdx%=_capacity;
            _sum -= _rollingWindow[_tailIdx];
            _size--;
        }

        _size++;
        var headIdx = HeadIdx;
        _rollingWindow[headIdx] = delay;
        _rollingWindowSetTime[headIdx] = now;
        _sum += _rollingWindow[headIdx];
    }

    public void AdjustMin(int delayMicros, uint now)
    {
        var headIdx = HeadIdx;
        int updateTo = Math.Min(delayMicros, _rollingWindow[headIdx]);
        AdjustCurrent(updateTo, now);
    }

    private void AdjustCurrent(int updateTo, uint now)
    {
        var headIdx = HeadIdx;
        _sum -= _rollingWindow[headIdx];
        _rollingWindow[headIdx] = updateTo;
        _rollingWindowSetTime[headIdx] = now;
        _sum += updateTo;
    }
}

// A UTP stream is the class that translate packets of UTP packet and pipe it into a System.Stream.
// IUTPTransfer is the abstraction that handles the underlying utp packet wrapping and such.
// It is expected that the sender will call WriteStream(Stream, CancellationToken) while the receiver
// will call ReadStream(Stream, CancellationToken).
// Most logic is handled within the WriteStream/ReadStream which makes concurrent states easier to handle
// at the expense of performance and latency, which is probably a bad idea, but at least its easy to make work.
//
// TODO: nagle
// TODO: Parameterized these
public class UTPStream(IUTPTransfer peer, ushort connectionId): IUTPTransfer
{
    // UDP packet that is for sure not going to be fragmented.
    // Not sure what to pick for the buffer size.
    // TODO: Maybe this can be dynamically adjusted?
    // TODO: Ethernet MTU is 1500
    const uint PAYLOAD_SIZE = 508;
    const int MAX_PAYLOAD_SIZE = 64000;

    const uint RECEIVE_WINDOW_SIZE = 128000;

    const int _resendDelayMicros = 100000;
    const int _waitForMessageDelayMs = 10;

    private uint _lastReceivedMicrosecond = 0;

    public LedBat _trafficControl = new LedBat(GetTimestamp());
    // m_cwnd
    // private uint EffectiveWindowSize => Math.Min(_trafficControl.WindowSize, _lastPacketFromPeer?.WindowSize ?? 1024);
    private uint EffectiveWindowSize => _trafficControl.WindowSize;

    private ConnectionState _state;
    private TaskCompletionSource _messageTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Need to be send in the header
    // The sequence number of the next packet we'll send (n_seq_nr).
    // Incremented by ST_SYN and ST_DATA
    private ushort _senderSequenceNumber = 0;

    // In a class so that it can atomically change.
    record AckInfo(ushort ackNumber, byte[]? selectiveAckData);

    // ACK info to be used when compiling header.
    private AckInfo _receiverAck = new AckInfo(0, null); // Mutated by receiver only

    private void SetInitialSequence(ushort initSequenceNumber, ushort initReceiveAck)
    {
        _senderSequenceNumber = initSequenceNumber;
        _receiverAck = new AckInfo(initReceiveAck, null);
    }

    #region Receiver portion

    private TaskCompletionSource<UTPPacketHeader> _syncTcs = new();

    // TODO: Use LRU instead. Need a special expiry handling so that the _incomingBufferSize is correct.
    private ConcurrentDictionary<ushort, Memory<byte>?> _receiveBuffer = new();
    private ulong _incomingBufferSize = 0;

    public async Task HandleReceiveHandshake(CancellationToken token)
    {
        UTPPacketHeader syncHeader = await _syncTcs.Task;
        SetInitialSequence((ushort)Random.Shared.Next(), syncHeader.SeqNumber);
        _state = ConnectionState.CsSynRecv;
    }

    public async Task ReadStream(Stream output, CancellationToken token)
    {
        bool finished = false;
        while (!finished || _state == ConnectionState.CsEnded)
        {
            token.ThrowIfCancellationRequested();

            AckInfo currentAckInfo = _receiverAck;

            ushort curAck = currentAckInfo.ackNumber;
            long packetIngested = 0;

            while (_receiveBuffer.TryRemove(WrappedAddOne(curAck), out Memory<byte>? packetData))
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
                    _receiverAck = new AckInfo(curAck, null);
                    await SendState(token);
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
            _receiverAck = new AckInfo(curAck, selectiveAck);

            await SendState(token);
            await WaitForMessage();
        }
    }

    public static byte[] CompileSelectiveAckBitset(ushort curAck, ConcurrentDictionary<ushort, Memory<byte>?> receiveBuffer)
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

    private async Task SendState(CancellationToken token)
    {
        UTPPacketHeader stateHeader = CreateBaseHeader(UTPPacketType.StState);
        await peer.ReceiveMessage(stateHeader, ReadOnlySpan<byte>.Empty, token);
    }

    private void ProcessSyn(UTPPacketHeader meta)
    {
        _syncTcs.TrySetResult(meta);
    }

    private void ProcessFin(UTPPacketHeader meta)
    {
        if (ShouldNotHandleSequence(meta, ReadOnlySpan<byte>.Empty)) return;

        _receiveBuffer[meta.SeqNumber] = null;
    }

    private bool ShouldNotHandleSequence(UTPPacketHeader meta, ReadOnlySpan<byte> data)
    {
        ushort receiverAck = _receiverAck.ackNumber;

        if (data.Length > MAX_PAYLOAD_SIZE)
        {
            return true;
        }

        // Got previous resend data probably
        if (IsLess(meta.SeqNumber, receiverAck))
        {
            Console.Error.WriteLine($"Ignored due to less than ack {meta.SeqNumber} {receiverAck}");
            return true;
        }

        // What if the sequence number is way too high. The threshold is estimated based on the window size
        // and the payload size
        if (!IsLess(meta.SeqNumber, (ushort)(receiverAck + RECEIVE_WINDOW_SIZE / PAYLOAD_SIZE)))
        {
            Console.Error.WriteLine($"Ignored due to too high sequnce {meta.SeqNumber} RA {receiverAck} T {(ushort)(receiverAck + RECEIVE_WINDOW_SIZE / PAYLOAD_SIZE)}");
            return true;
        }

        // No space in buffer UNLESS its the next needed sequence, in which case, we still process it, otherwise
        // it can get stuck.
        if (_incomingBufferSize + (ulong)data.Length > RECEIVE_WINDOW_SIZE && meta.SeqNumber != WrappedAddOne(receiverAck))
        {
            // Attempt to clean incoming buffer.
            // Ideally, it just use an LRU instead.
            List<ushort> keptBuffers = _receiveBuffer.Select(kv => kv.Key).ToList();
            foreach (var keptBuffer in keptBuffers)
            {
                // Can happen sometime, the buffer is written at the same time as it is being processed so the ack
                // is not updated yet at that point.
                if (IsLess(keptBuffer, receiverAck))
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
                ushort minKept = (ushort)(_senderSequenceNumber + 65);
                int i = 0;
                while (i < keptBuffers.Count && _incomingBufferSize > targetSize)
                {
                    if (IsLess(keptBuffers[i], minKept)) continue; // Must keep at least 65 items as we could ack them via selective ack
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

    private void ProcessStData(UTPPacketHeader meta, ReadOnlySpan<byte> data)
    {
        if (ShouldNotHandleSequence(meta, data)) return;

        // TODO: Fast path without going to receive buffer?
        _incomingBufferSize += (ulong)data.Length;
        _receiveBuffer[meta.SeqNumber] = data.ToArray();
    }

    #endregion

    #region Sender portion


    // m_bytes_in_flight
    long _inflightData = 0;

    // Well... technically I want a deque
    // The head's Header.SeqNumber is analogous to m_acked_seq_nr
    LinkedList<UnackedItem> unackedWindow = new LinkedList<UnackedItem>();


    private UTPPacketHeader? _lastPacketFromPeer; // Also contains the ack, n_acked_seq_nr

    private void ProcessAck(UTPPacketHeader meta)
    {
        UTPPacketHeader? lastPacketFromPeer = _lastPacketFromPeer;
        if (lastPacketFromPeer == null || IsLessOrEqual(lastPacketFromPeer.AckNumber, meta.AckNumber))
        {
            // Note, even if equal, we reset it because selective ack
            _lastPacketFromPeer = meta;
        }
    }

    public async Task InitiateHandshake(CancellationToken token)
    {
        // TODO: MTU probe
        // TODO: in libp2p this is added to m_outbuf, and therefore part of the
        // resend logic

        _senderSequenceNumber = 64000; // Now, for some reason, tis is one, but from the other side, its random?
        UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StSyn);
        _senderSequenceNumber++;
        _state = ConnectionState.CsSynSent;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await peer.ReceiveMessage(header, ReadOnlySpan<byte>.Empty, token);
            await WaitForMessage();
            if (_state == ConnectionState.CsConnected) break;
        }
    }

    class UnackedItem(UTPPacketHeader header, Memory<byte> buffer)
    {
        public UTPPacketHeader Header => header;
        public bool AssumedLoss { get; set; }
        public int UnackedCounter { get; set; }
        public Memory<byte> Buffer => buffer;
    }

    public async Task WriteStream(Stream input, CancellationToken token)
    {
        bool streamFinished = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();

            UTPPacketHeader? ackHeader = _lastPacketFromPeer;
            if (ackHeader != null) {
                ulong initialWindowSize = (ulong)_inflightData;
                ProcessAckedEntry(ackHeader);
                ulong ackedBytes = initialWindowSize - (ulong)_inflightData;
                _trafficControl.OnAck(ackedBytes, initialWindowSize, ackHeader.TimestampDeltaMicros, GetTimestamp());
            }

            await Retransmit(unackedWindow, token);
            await IngestStream();

            if (streamFinished && unackedWindow.Count == 0)
            {
                break;
            }

            await WaitForMessage();

        }

        void ProcessAckedEntry(UTPPacketHeader ackHeader)
        {
            ushort ackNumberPlus1 = WrappedAddOne(ackHeader.AckNumber);
            Console.Error.WriteLine($"S process ack {ackHeader}");
            LinkedListNode<UnackedItem>? unackedHead = unackedWindow.First;
            while (true)
            {
                if (unackedHead == null) return;

                if (!IsLess(unackedHead.Value.Header.SeqNumber, ackNumberPlus1))
                {
                    // Seq is more than ack
                    break;
                }

                _inflightData -= unackedHead.Value.Buffer.Length;
                var toRemove = unackedHead;
                Console.Error.WriteLine($"S acked {toRemove.Value.Header.SeqNumber}");
                unackedHead = unackedHead.Next;
                unackedWindow.Remove(toRemove);
            }

            if (ackHeader.SelectiveAck == null) return;

            // Right next after ack number is inferred to be unacked. We dont care about that case.
            if (unackedHead?.Value.Header.SeqNumber == ackNumberPlus1)
            {
                unackedHead = unackedHead?.Next;
            }

            for (var i = 0; i < ackHeader.SelectiveAck.Length; i++)
            {
                if (unackedHead == null) return;
                ushort ackNumber = (ushort)(ackHeader.AckNumber + 2 + i * 8);
                byte ackByte = ackHeader.SelectiveAck[i];

                if (IsLess((ushort)(ackNumber + 7), unackedHead.Value.Header.SeqNumber))
                {
                    // Skip this byte completely
                    // TODO: Just change i directly to the right seqnumber
                    continue;
                }


                for (int j = 0; j < 8; j++)
                {
                    if (unackedHead == null) return;

                    if (IsLess(unackedHead.Value.Header.SeqNumber, ackNumber))
                    {
                        Debug.Fail("Sequence number not incremented correctly");
                    }
                    else if (unackedHead.Value.Header.SeqNumber == ackNumber)
                    {
                        if ((ackByte & 1) != 0)
                        {
                            _inflightData -= unackedHead.Value.Buffer.Length;
                            var toRemove = unackedHead;
                            Console.Error.WriteLine($"S sel acked {toRemove.Value.Header.SeqNumber}");
                            unackedHead = unackedHead.Next;
                            unackedWindow.Remove(toRemove);
                        }
                        else
                        {
                            unackedHead = unackedHead.Next;
                        }
                    }

                    ackNumber++;
                    ackByte = (byte)(ackByte >> 1);
                }
            }

            Console.Error.WriteLine($"S sel ack check complete");
        }

        async Task IngestStream()
        {
            while (!streamFinished && _inflightData + PAYLOAD_SIZE < EffectiveWindowSize)
            {
                byte[] buffer = new byte[PAYLOAD_SIZE];

                int readLength = await input.ReadAsync(buffer, token);
                if (readLength != 0) // Note: We assume ReadAsync will return 0 multiple time.
                {
                    await SendPacket(UTPPacketType.StData, buffer.AsMemory()[..readLength]);
                }
                else
                {
                    // Send FIN
                    await SendPacket(UTPPacketType.StFin, Memory<byte>.Empty);
                    streamFinished = true;
                }
            }
        }

        async Task SendPacket(UTPPacketType packetType, Memory<byte> asMemory)
        {
            UTPPacketHeader header = CreateBaseHeader(packetType);
            if (packetType == UTPPacketType.StData)
            {
                _senderSequenceNumber++;
            }
            Console.Error.WriteLine($"S Send {header.SeqNumber} {_inflightData} {EffectiveWindowSize}");
            unackedWindow.AddLast(new UnackedItem(header, asMemory));
            await peer.ReceiveMessage(header, asMemory.Span, token);
            _inflightData += asMemory.Length;
        }

    }

    async Task Retransmit(LinkedList<UnackedItem> unackedWindow, CancellationToken token)
    {
        UTPPacketHeader? lastPacketFromPeer = _lastPacketFromPeer;
        if (lastPacketFromPeer == null)
        {
            Console.Error.WriteLine($"S What?");
            return;
        }

        var nextUnackedEntry = unackedWindow.First;
        if (nextUnackedEntry == null) {
            Console.Error.WriteLine($"S Unacked empty");
            // No unacked window
            return;
        }

        // Well... ideally this obtained right during send.
        uint nowMicros = GetTimestamp();
        LinkedListNode<UnackedItem>? curUnackedWindowHead = nextUnackedEntry;
        if (IsLessOrEqual(curUnackedWindowHead.Value.Header.SeqNumber, lastPacketFromPeer.AckNumber))
        {
            // Something weird happen here. Could be a new ack that just come in at point curUnackedWindowHead is updated,
            // in which case, just exit, the send loop will re-ingest it again.
            Console.Error.WriteLine($"S Weird caste {curUnackedWindowHead.Value.Header.SeqNumber} {lastPacketFromPeer.AckNumber}");
            return;
        }

        if (lastPacketFromPeer.SelectiveAck == null)
        {
            UTPPacketHeader nextHeader = nextUnackedEntry.Value.Header;
            ushort ackNumber = lastPacketFromPeer.AckNumber;
            if (WrappedAddOne(ackNumber) == nextHeader.SeqNumber)
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

                if (IsLess(seqNum, curUnackedWindowHead.Value.Header.SeqNumber)) continue;
                Debug.Assert(curUnackedWindowHead.Value.Header.SeqNumber == seqNum); // It should not be more. Or else something is broken.

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
            unackedItem.UnackedCounter+=unackedCount;
            if (unackedItem.UnackedCounter < 3)
            {
                // Fast Retransmit.
                Console.Error.WriteLine($"S not retransmit {unackedItem.Header} because of unacked counter {unackedItem.UnackedCounter}");
                // TODO: check this logic https://github.com/arvidn/libtorrent/blob/9aada93c982a2f0f76b129857bec3f885a37c437/include/libtorrent/aux_/utp_stream.hpp#L948
                return true;
            }

            if (!unackedItem.AssumedLoss)
            {
                _inflightData -= unackedItem.Buffer.Length;
                unackedItem.AssumedLoss = true;
            }

            // TODO: Think again about using a delay to prevent resending ack too early. Think about the inflight data
            // may have just throttle it fine probably.
            // TODO: make overflow safe
            UTPPacketHeader header = unackedItem.Header;
            if (_inflightData + unackedItem.Buffer.Length >= EffectiveWindowSize)
            {
                Console.Error.WriteLine($"S Not retransmit {header.SeqNumber} due to window limit {_inflightData} {EffectiveWindowSize}");
                return false;
            }

            RefreshHeader(header);
            Console.Error.WriteLine($"S Retransmit {header.SeqNumber}");
            // Resend it
            _inflightData += unackedItem.Buffer.Length;
            unackedItem.AssumedLoss = false;
            unackedItem.UnackedCounter = 0;
            await peer.ReceiveMessage(header, unackedItem.Buffer.Span, token);

            _trafficControl.OnDataLoss(GetTimestamp());

            return true;
        }
    }


    #endregion

    private UTPPacketHeader CreateBaseHeader(UTPPacketType type)
    {
        UTPPacketHeader header = new UTPPacketHeader()
        {
            PacketType = type,
            Version = 1,
            ConnectionId = connectionId,
            WindowSize = _trafficControl.WindowSize,
            SeqNumber = _senderSequenceNumber,
        };

        RefreshHeader(header);

        return header;
    }

    private void RefreshHeader(UTPPacketHeader header)
    {
        AckInfo ackInfo = _receiverAck;

        uint timestamp = GetTimestamp();
        header.TimestampMicros = timestamp;

        // TODO: double check m_reply_micro logic
        header.TimestampDeltaMicros = timestamp - _lastReceivedMicrosecond;

        header.AckNumber = ackInfo.ackNumber;
        header.SelectiveAck = ackInfo.selectiveAckData;
    }

    private static uint GetTimestamp()
    {
        long ticks = Stopwatch.GetTimestamp();
        long microseconds = (ticks * 1_000_000) / Stopwatch.Frequency;
        return (uint)microseconds;
    }

    private bool IsLess(ushort num1, ushort num2)
    {
        // Why do I think there is  a better way of doing this?
        return (num1 + 32768) % 65536 < (num2 + 32768) % 65536;
    }

    private bool IsLessOrEqual(ushort num1, ushort num2)
    {
        return IsLess(num1, WrappedAddOne(num2));
    }

    private ushort WrappedAddOne(ushort num)
    {
        // Why do I think there is  a better way of doing this?
        return (ushort)(num + 1);
    }

    private async Task<bool> WaitForMessage()
    {
        var delay = Task.Delay(_waitForMessageDelayMs);
        if (await Task.WhenAny(_messageTcs.Task, delay) == delay)
        {
            return false;
        }

        _messageTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return true;
    }

    public Task ReceiveMessage(UTPPacketHeader meta, ReadOnlySpan<byte> data, CancellationToken token)
    {
        // TODO: Probably should be handled at deserializer level
        if (meta.Version != 1) return Task.CompletedTask;

        // TODO: When EOF was obtained from FIN, filter out sequence higher than that.
        _lastReceivedMicrosecond = meta.TimestampMicros;

        ProcessAck(meta);

        switch (meta.PacketType)
        {
            case UTPPacketType.StSyn:
                ProcessSyn(meta);
                break;


            case UTPPacketType.StState:
                if (_state == ConnectionState.CsSynSent) _state = ConnectionState.CsConnected;
                // Otherwise most logic is in ProcessAck.
                break;

            case UTPPacketType.StFin:
                ProcessFin(meta);

                if (_state == ConnectionState.CsEnded)
                {
                    // Special case, once FIN is first received, the reader stream thread would have exited after its ACK.
                    // In the case where the last ACK is lost, the sender send FIN again, so this need to happen.
                    _ = SendState(token);
                }
                break;

            case UTPPacketType.StData:
                if (_state == ConnectionState.CsSynRecv) _state = ConnectionState.CsConnected;
                ProcessStData(meta, data);
                break;

            case UTPPacketType.StReset:
                _state = ConnectionState.CsEnded;
                break;
        }

        _messageTcs.TrySetResult();

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

}


