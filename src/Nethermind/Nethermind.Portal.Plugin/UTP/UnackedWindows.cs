// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using Nethermind.Logging;

namespace Nethermind.Network.Portal.UTP;

public class UnackedWindows(ILogger logger)
{

    private ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private long _inflightData = 0;
    private readonly LinkedList<UnackedItem> _unackedWindow = new LinkedList<UnackedItem>();

    public int ProcessAck(UTPPacketHeader lastPacketHeaderSent, uint now)
    {
        var ackedBytes = 0;
        var ackNumberPlus1 = UTPUtil.WrappedAddOne(lastPacketHeaderSent.AckNumber);
        LinkedListNode<UnackedItem>? unackedHead = _unackedWindow.First;
        while (true)
        {
            if (unackedHead == null) return ackedBytes;

            // there are packages on the list that are sent but not acked so they are still in transit.
            if (!UTPUtil.IsLess(unackedHead.Value.Header.SeqNumber, ackNumberPlus1)) break;

            if (logger.IsTrace) logger.Trace($"S acked {unackedHead.Value.Header.SeqNumber}");

            unackedHead = RemodeUnackedEntry(unackedHead, now, ref ackedBytes);
        }

        if (lastPacketHeaderSent.SelectiveAck == null) return ackedBytes;

        // Right next after ack number is inferred to be unacked. We dont care about that case.
        if (unackedHead?.Value.Header.SeqNumber == ackNumberPlus1)
            unackedHead = unackedHead?.Next;

        ackedBytes += ProcessSelectiveAck(lastPacketHeaderSent, unackedHead, now);
        return ackedBytes;
    }

    private int ProcessSelectiveAck(UTPPacketHeader lastPacketSent, LinkedListNode<UnackedItem>? unackedHead, uint now)
    {
        var ackedBytes = 0;
        var headerAckNumber = lastPacketSent.AckNumber;
        var selectiveAck = lastPacketSent.SelectiveAck;
        if (selectiveAck == null) return ackedBytes;
        for (var i = 0; i < selectiveAck.Length; i++)
        {
            if (unackedHead == null) return ackedBytes;
            var ackNumber = (ushort)(headerAckNumber + 2 + i * 8);
            var ackByte = selectiveAck[i];

            if (UTPUtil.IsLess((ushort)(ackNumber + 7), unackedHead.Value.Header.SeqNumber))
                // Skip this byte completely
                // TODO: Just change i directly to the right seqnumber
                continue;


            for (var j = 0; j < 8; j++)
            {
                if (unackedHead == null) return ackedBytes;

                if (UTPUtil.IsLess(unackedHead.Value.Header.SeqNumber, ackNumber))
                    Debug.Fail("Sequence number not incremented correctly");
                else if (unackedHead.Value.Header.SeqNumber == ackNumber)
                {
                    if ((ackByte & 1) != 0)
                    {
                        if (logger.IsTrace) logger.Trace($"S sel acked {unackedHead.Value.Header.SeqNumber}");

                        unackedHead = RemodeUnackedEntry(unackedHead, now, ref ackedBytes);
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

        return ackedBytes;
    }

    private LinkedListNode<UnackedItem>? RemodeUnackedEntry(LinkedListNode<UnackedItem> unackedHead, uint now, ref int ackedBytes)
    {
        _inflightData -= unackedHead!.Value.Buffer.Count;
        var toRemove = unackedHead;
        ackedBytes += toRemove.Value.Buffer.Count;
        OnAck(toRemove.Value, now);
        var next = unackedHead.Next;
        _unackedWindow.Remove(toRemove);

        return next;
    }

    private uint rtt = 0;
    private uint rttVar = 0;
    private uint rto = 0;
    public uint rtoTimeout = 0;

    private void OnAck(UnackedItem entry, uint now)
    {
        // Update rtt
        if (entry.TransmitCount == 1)
        {
            var packetRtt = now - entry.SentTime;
            if (rtt == 0)
            {
                rtt = packetRtt;
                rttVar = packetRtt / 2;
            }
            else
            {
                var delta = (int)rtt - (int)packetRtt;
                rttVar = (uint)(rttVar + (int)(int.Abs(delta) - rttVar) / 4);
                rtt = rtt - rtt / 8 + packetRtt / 8;
            }

            rto = Math.Max(rtt + rttVar * 4, 1000000);
        }

        rtoTimeout = now + rto;
    }

    public ulong GetCurrentUInflightData()
    {
        return (ulong)_inflightData;
    }

    public long GetCurrentInflightData()
    {
        return _inflightData;
    }

    private void IncrementInflightData(int increment)
    {
        _inflightData += increment;
    }

    private void DecrementInflightData(int decrement)
    {
        _inflightData -= decrement;
    }

    public bool isUnackedWindowEmpty()
    {
        return _unackedWindow.Count == 0;
    }

    public void TrackPacket(ArraySegment<byte> asMemory, UTPPacketHeader header, uint now)
    {
        _unackedWindow.AddLast(new UnackedItem(header, asMemory, now));
        IncrementInflightData(asMemory.Count);
    }

    public LinkedList<UnackedItem> getUnAckedWindow()
    {
        return _unackedWindow;
    }

    public enum OnUnackResult
    {
        Retransmit,
        WindowFull,
        No
    }

    public OnUnackResult OnUnack(UnackedItem unackedItem, int unackedCount, uint currentWindowSize)
    {
        unackedItem.UnackedCounter += unackedCount;
        if (unackedItem.UnackedCounter < 3)
            // We assume its not loss yet
            return OnUnackResult.No;

        if (!unackedItem.AssumedLoss)
        {
            // So once its assumed loss, we mark it so that we don't subtract inflight data twice
            DecrementInflightData(unackedItem.Buffer.Count);
            unackedItem.AssumedLoss = true;
        }

        if (GetCurrentInflightData() + unackedItem.Buffer.Count > currentWindowSize)
            // So we have determined to retransmit, but not enough window
            // this could happen for example, when the window size goes down.
            return OnUnackResult.WindowFull;

        // Retransmit, so we
        IncrementInflightData(unackedItem.Buffer.Count);
        unackedItem.AssumedLoss = false;
        unackedItem.NeedResent = true;
        unackedItem.UnackedCounter = 0;
        return OnUnackResult.Retransmit;
    }

    public void OnRtoTimeout()
    {
        var linkedListNode = _unackedWindow.First;
        while (linkedListNode != null)
        {
            var entry = linkedListNode.Value;
            if (entry.TransmitCount != 0)
                entry.NeedResent = true;
            linkedListNode = linkedListNode.Next;
        }
    }
}
