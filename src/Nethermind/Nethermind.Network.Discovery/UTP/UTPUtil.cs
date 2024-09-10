// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using NonBlocking;

namespace Nethermind.Network.Discovery.UTP;

public class UTPUtil
{
    public static ushort WrappedAddOne(ushort num) {
        return (ushort)(num + 1);
    }
    public static bool IsLessOrEqual(ushort num1, ushort num2) {
        return IsLess(num1, WrappedAddOne(num2));
    }

    public static bool IsLess(ushort num1, ushort num2) {
        // Why do I think there is  a better way of doing this?
        return (num1 + 32768) % 65536 < (num2 + 32768) % 65536;
    }

    public static uint GetTimestamp() {
        long ticks = Stopwatch.GetTimestamp();
        long microseconds = (ticks * 1) / (Stopwatch.Frequency / 1_000_000);
        return (uint)microseconds;
    }

    public static byte[]? CompileSelectiveAckBitset(ushort curAck, ConcurrentDictionary<ushort, ArraySegment<byte>?> receiveBuffer) {
        if (receiveBuffer.Count == 0)
        {
            return null;
        }

        // Fixed 64 bit.
        // TODO: use long
        // TODO: no need to encode trailing zeros
        byte[] selectiveAck = new byte[8];

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
}
