// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Network.Discovery;

public class InflightDataCalculator
{
    // m_bytes_in_flight
    long _inflightData = 0;

    public  void CalculateInflightData(UTPPacketHeader lastPacketHeaderFromPeer, LinkedList<UnackedItem> unackedWindow)
    {
            ushort ackNumberPlus1 = UTPUtil.WrappedAddOne(lastPacketHeaderFromPeer.AckNumber);
            Console.Error.WriteLine($"S process ack {lastPacketHeaderFromPeer}");
            LinkedListNode<UnackedItem>? unackedHead = unackedWindow.First;
            while (true)
            {
                if (unackedHead == null) return;

                if (!UTPUtil.IsLess(unackedHead.Value.Header.SeqNumber, ackNumberPlus1))
                {
                    // Seq is more than ack
                    break;
                }

                _inflightData -= unackedHead.Value.Buffer.Length;
                var toRemove = unackedHead;
                Console.Error.WriteLine($"S acked {unackedHead.Value.Header.SeqNumber}");
                unackedHead = unackedHead.Next;
                unackedWindow.Remove(toRemove);
            }

            if (lastPacketHeaderFromPeer.SelectiveAck == null) return;

            // Right next after ack number is inferred to be unacked. We dont care about that case.
            if (unackedHead?.Value.Header.SeqNumber == ackNumberPlus1)
            {
                unackedHead = unackedHead?.Next;
            }

            for (var i = 0; i < lastPacketHeaderFromPeer.SelectiveAck.Length; i++)
            {
                if (unackedHead == null) return;
                ushort ackNumber = (ushort)(lastPacketHeaderFromPeer.AckNumber + 2 + i * 8);
                byte ackByte = lastPacketHeaderFromPeer.SelectiveAck[i];

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

            Console.Error.WriteLine($"S sel ack check complete");
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
}
