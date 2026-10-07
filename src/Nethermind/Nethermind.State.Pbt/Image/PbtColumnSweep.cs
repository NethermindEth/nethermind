// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>Bounds for sweeping a sorted PBT column in chunks, one short-lived view per chunk.</summary>
internal static class PbtColumnSweep
{
    /// <summary>An exclusive upper bound past every PBT column key, including the storage keys longer than 32 bytes.</summary>
    public static byte[] PastEveryKey()
    {
        byte[] key = new byte[PbtStorageTreeKey.MaxLength + 1];
        key.AsSpan().Fill(0xFF);
        return key;
    }

    /// <summary>Returns the inclusive lower bound immediately after <paramref name="key"/>.</summary>
    public static byte[] AfterKey(ReadOnlySpan<byte> key)
    {
        byte[] next = new byte[key.Length + 1];
        key.CopyTo(next);
        return next;
    }
}
