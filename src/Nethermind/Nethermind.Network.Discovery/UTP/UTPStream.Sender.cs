// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Network.Discovery.UTP;

public partial class UTPStream
{
    // Used for ack
    private UTPPacketHeader? _peerAck;

    private ushort _seq_nr = 0; // Incremented by ST_SYN and ST_DATA

    private readonly UnackedWindows _unackedWindows = new UnackedWindows(logManager.GetClassLogger<UTPStream>());
    private readonly LEDBAT _trafficControl = new LEDBAT(logManager);

    private TaskCompletionSource _ackTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task WriteStream(Stream input, CancellationToken token)
    {
        bool streamFinished = false;
        while (true)
        {
            if (_logger.IsTrace) _logger.Trace("S loop start");
            token.ThrowIfCancellationRequested();

            uint now = UTPUtil.GetTimestamp();
            if (_peerAck != null)
            {
                ulong initialWindowSize = _unackedWindows.GetCurrentUInflightData();
                ulong ackedBytes = (ulong) _unackedWindows.ProcessAck(_peerAck, now);
                _trafficControl.OnAck(ackedBytes, initialWindowSize, _peerAck.TimestampDeltaMicros, UTPUtil.GetTimestamp());
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
                ArraySegment<byte> buffer = new ArraySegment<byte>(_arrayPool.Rent(PayloadSize), 0, PayloadSize);
                int readLength = await input.ReadAsync(buffer, token);
                buffer = buffer[..readLength];

                if (readLength != 0) {  // Note: We assume ReadAsync will return 0 multiple time.
                    UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StData);
                    _unackedWindows.TrackPacket(buffer, header, UTPUtil.GetTimestamp());
                    _seq_nr++;
                }else {
                    _arrayPool.Return(buffer.Array!);

                    UTPPacketHeader header = CreateBaseHeader(UTPPacketType.StFin);
                    _unackedWindows.TrackPacket(ArraySegment<byte>.Empty, header, UTPUtil.GetTimestamp());
                    if (_logger.IsTrace) _logger.Trace($"S stream finished. Fin sent. {_seq_nr}");
                    streamFinished = true;
                }
            }

            await FlushPackets(token);
            if (streamFinished && _unackedWindows.isUnackedWindowEmpty()) break;

            await Task.WhenAny(_ackTcs.Task, Task.Delay(100, token));
            _ackTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                RefreshHeader(header);
                await peer.ReceiveMessage(header, entry.Buffer.AsSpan(), token);
            }
            linkedListNode = linkedListNode.Next;
        }
    }

    private void RecordAck(UTPPacketHeader packageHeader)
    {
        if (_peerAck == null ||  UTPUtil.IsLessOrEqual(_peerAck.AckNumber, packageHeader.AckNumber)) {
            _peerAck = packageHeader;

            _ackTcs.TrySetResult();
        }
    }

    private bool isSpaceAvailableOnStream()
    {
        return _unackedWindows.GetCurrentInflightData() + PayloadSize < _trafficControl.WindowSize;
    }

    private void Retransmit(LinkedList<UnackedItem> unackedWindow)
    {
        // if (_logger.IsTrace) _logger.Trace($"S Retransmit");
        UTPPacketHeader? lastPacketFromPeer = _peerAck;
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

            if (UTPUtil.WrappedAddOne(lastPacketFromPeer.AckNumber) == curUnackedWindowHead.Value.Header.SeqNumber)
            {
                // The ackNumber+1 is always unacked when SelectiveAck is set.
                // 0 here then refers to ack+2
                MaybeRetransmit(curUnackedWindowHead.Value, ackedAfterCumul[0]);
            }

            curUnackedWindowHead = curUnackedWindowHead.Next;
            for (int i = 0; i < totalBits && curUnackedWindowHead != null && retransmitCount < SELACK_MAX_RESEND; i++)
            {
                ushort seqNum = (ushort)(lastPacketFromPeer.AckNumber + 2 + i);

                if (UTPUtil.IsLess(seqNum, curUnackedWindowHead.Value.Header.SeqNumber))
                {
                    // This seq number is less. It could be that it was acked before.
                    continue;
                }

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

}
