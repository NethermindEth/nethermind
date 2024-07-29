// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Network.Discovery;

public class InflightDataCalculator {
    private long _inflightData = 0;
    private LinkedList<UnackedItem> unackedWindow = new LinkedList<UnackedItem>();

    public  void CalculateInflightData(UTPPacketHeader lastPacketHeaderSent)
    {
            ushort ackNumberPlus1 = UTPUtil.WrappedAddOne(lastPacketHeaderSent.AckNumber);
            LinkedListNode<UnackedItem>? unackedHead = unackedWindow.First;
            while (true)
            {
                if (unackedHead == null) return;

                // there are packages on the list that are sent but not acked so they are still in transit.
                if (!UTPUtil.IsLess(unackedHead.Value.Header.SeqNumber, ackNumberPlus1)) break;

                _inflightData -= unackedHead.Value.Buffer.Length;
                var toRemove = unackedHead;
                Console.Error.WriteLine($"S acked {unackedHead.Value.Header.SeqNumber}");
                unackedHead = unackedHead.Next;
                unackedWindow.Remove(toRemove);
            }

            if (lastPacketHeaderSent.SelectiveAck == null) return;

            // Right next after ack number is inferred to be unacked. We dont care about that case.
            if (unackedHead?.Value.Header.SeqNumber == ackNumberPlus1)
            {
                unackedHead = unackedHead?.Next;
            }
            CalculateInFlighDataBySelectiveACK(lastPacketHeaderSent, unackedWindow, unackedHead);
    }

    private void CalculateInFlighDataBySelectiveACK(UTPPacketHeader lastPacketSent, LinkedList<UnackedItem> unackedWindow, LinkedListNode<UnackedItem>? unackedHead)
    {
        if (lastPacketSent.SelectiveAck == null) return;
        for (var i = 0; i < lastPacketSent.SelectiveAck.Length; i++)
        {
            if (unackedHead == null) return;
            ushort ackNumber = (ushort)(lastPacketSent.AckNumber + 2 + i * 8);
            byte ackByte = lastPacketSent.SelectiveAck[i];

            if (UTPUtil.IsLess((ushort)(ackNumber + 7), unackedHead.Value.Header.SeqNumber))
            {
                // Skip this byte completely
                // TODO: Just change i directly to the right seqnumber
                continue;
            }


            for (int j = 0; j < 8; j++)
            {
                if (unackedHead == null) return;

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
    }

    public ulong GetCurrentUInflightData()
    {
        return (ulong)_inflightData;
    }

    public long GetCurrentInflightData()
    {
        return _inflightData;
    }

    public void IncrementInflightData(int increment)
    {
        _inflightData += increment;
    }

    public void DecrementInflightData(int decrement)
    {
        _inflightData -= decrement;
    }

    public bool isUnackedWindowEmpty()
    {
         return unackedWindow.Count == 0;
    }

    public void trackPacket(Memory<byte> asMemory, UTPPacketHeader header)
    {
        unackedWindow.AddLast(new UnackedItem(header, asMemory));
    }

    public LinkedList<UnackedItem> getUnAckedWindow()
    {
        return unackedWindow;
    }
}
