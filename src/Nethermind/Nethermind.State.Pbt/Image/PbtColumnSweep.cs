// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>Bounds for sweeping a whole sorted PBT column.</summary>
internal static class PbtColumnSweep
{
    /// <summary>An exclusive upper bound past every PBT column key, including the storage keys longer than 32 bytes.</summary>
    public static byte[] PastEveryKey()
    {
        byte[] key = new byte[PbtVariableTreeKey.MaxLength + 1];
        key.AsSpan().Fill(0xFF);
        return key;
    }
}
