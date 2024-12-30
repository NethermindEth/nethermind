// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Network.Portal.UTP;

public class UTPUtil
{
    public static ushort WrappedAddOne(ushort num)
    {
        return (ushort)(num + 1);
    }
    public static bool IsLessOrEqual(ushort num1, ushort num2)
    {
        return IsLess(num1, WrappedAddOne(num2));
    }

    public static bool IsLess(ushort num1, ushort num2)
    {
        // Why do I think there is  a better way of doing this?
        return (num1 + 32768) % 65536 < (num2 + 32768) % 65536;
    }

    public static uint GetTimestamp()
    {
        var ticks = Stopwatch.GetTimestamp();
        var microseconds = ticks * 1 / (Stopwatch.Frequency / 1_000_000);
        return (uint)microseconds;
    }

}
