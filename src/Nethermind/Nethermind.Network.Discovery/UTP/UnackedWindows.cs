// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery;

public class UnackedWindows (ILogger logger) {
    private long _inflightData = 0;
    private LinkedList<UnackedItem> unackedWindow = new LinkedList<UnackedItem>();

    public int ProcessAck(UTPPacketHeader lastPacketHeaderSent, uint now)
    {
        int ackedBytes = 0;
        ushort ackNumberPlus1 = UTPUtil.WrappedAddOne(lastPacketHeaderSent.AckNumber);
        LinkedListNode<UnackedItem>? unackedHead = unackedWindow.First;
        while (true)
        {
            if (unackedHead == null) return ackedBytes;

            // there are packages on the list that are sent but not acked so they are still in transit.
            if (!UTPUtil.IsLess(unackedHead.Value.Header.SeqNumber, ackNumberPlus1)) break;

            _inflightData -= unackedHead.Value.Buffer.Length;
            var toRemove = unackedHead;
            if (logger.IsTrace) logger.Trace($"S acked {unackedHead.Value.Header.SeqNumber}");
            ackedBytes += toRemove.Value.Buffer.Length;
            OnAck(toRemove.Value, now);
            unackedHead = unackedHead.Next;
            unackedWindow.Remove(toRemove);
        }

        if (lastPacketHeaderSent.SelectiveAck == null) return ackedBytes;

        // Right next after ack number is inferred to be unacked. We dont care about that case.
        if (unackedHead?.Value.Header.SeqNumber == ackNumberPlus1)
        {
            unackedHead = unackedHead?.Next;
        }

        ackedBytes += ProcessSelectiveAck(lastPacketHeaderSent, unackedHead, now);
        return ackedBytes;
    }

    private int ProcessSelectiveAck(UTPPacketHeader lastPacketSent, LinkedListNode<UnackedItem>? unackedHead, uint now)
    {
        int ackedBytes = 0;
        var headerAckNumber = lastPacketSent.AckNumber;
        var selectiveAck = lastPacketSent.SelectiveAck;
        if (selectiveAck == null) return ackedBytes;
        for (var i = 0; i < selectiveAck.Length; i++)
        {
            if (unackedHead == null) return ackedBytes;
            ushort ackNumber = (ushort)(headerAckNumber + 2 + i * 8);
            byte ackByte = selectiveAck[i];

            if (UTPUtil.IsLess((ushort)(ackNumber + 7), unackedHead.Value.Header.SeqNumber))
            {
                // Skip this byte completely
                // TODO: Just change i directly to the right seqnumber
                continue;
            }


            for (int j = 0; j < 8; j++)
            {
                if (unackedHead == null) return ackedBytes;

                if (UTPUtil.IsLess(unackedHead.Value.Header.SeqNumber, ackNumber))
                {
                    Debug.Fail("Sequence number not incremented correctly");
                }
                else if (unackedHead.Value.Header.SeqNumber == ackNumber)
                {
                    if ((ackByte & 1) != 0)
                    {
                        _inflightData -= unackedHead.Value.Buffer.Length;
                        var toRemove = unackedHead;
                        ackedBytes += toRemove.Value.Buffer.Length;
                        OnAck(toRemove.Value, now);
                        if (logger.IsTrace) logger.Trace($"S sel acked {toRemove.Value.Header.SeqNumber}");
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

        return ackedBytes;
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
                int delta = (int)rtt - (int)packetRtt;
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
        return unackedWindow.Count == 0;
    }

    public void trackPacket(Memory<byte> asMemory, UTPPacketHeader header, uint now)
    {
        unackedWindow.AddLast(new UnackedItem(header, asMemory, now));
        IncrementInflightData(asMemory.Length);
    }

    public LinkedList<UnackedItem> getUnAckedWindow()
    {
        return unackedWindow;
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
        {
            // We assume its not loss yet
            return OnUnackResult.No;
        }

        if (!unackedItem.AssumedLoss)
        {
            // So once its assumed loss, we mark it so that we don't subtract inflight data twice
            DecrementInflightData(unackedItem.Buffer.Length);
            unackedItem.AssumedLoss = true;
        }

        if (GetCurrentInflightData() + unackedItem.Buffer.Length > currentWindowSize)
        {
            // So we have determined to retransmit, but not enough window
            // this could happen for example, when the window size goes down.
            return OnUnackResult.WindowFull;
        }

        // Retransmit, so we
        IncrementInflightData(unackedItem.Buffer.Length);
        unackedItem.AssumedLoss = false;
        unackedItem.NeedResent = true;
        unackedItem.UnackedCounter = 0;
        return OnUnackResult.Retransmit;
    }

    public void OnRtoTimeout()
    {
        var linkedListNode = unackedWindow.First;
        while (linkedListNode != null)
        {
            var entry = linkedListNode.Value;
            if (entry.TransmitCount != 0)
            {
                entry.NeedResent = true;
            }
            linkedListNode = linkedListNode.Next;
        }
    }
}
