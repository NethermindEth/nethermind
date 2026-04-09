// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.StateComposition;

/// <summary>
/// Per-block depth distribution delta produced by <see cref="TrieDiffWalker"/>.
/// Values can be negative (nodes removed). Reused across diff calls via <see cref="Clear"/>.
/// </summary>
public sealed class DepthDelta
{
    public readonly long[] AccountFullNodes  = new long[16];
    public readonly long[] AccountShortNodes = new long[16]; // extensions + leaves at depth
    public readonly long[] AccountValueNodes = new long[16]; // leaves at physical depth (unshifted)
    public readonly long[] AccountNodeBytes  = new long[16];

    public readonly long[] StorageFullNodes  = new long[16];
    public readonly long[] StorageShortNodes = new long[16];
    public readonly long[] StorageValueNodes = new long[16];
    public readonly long[] StorageNodeBytes  = new long[16];

    public readonly long[] BranchOccupancy  = new long[16]; // index i = branches with (i+1) children
    public long TotalBranchNodesDelta;
    public long TotalBranchChildrenDelta;

    /// <summary>
    /// Returns true when every array element and both scalars are zero.
    /// Used to skip the 149-setter <see cref="Metrics.UpdateFromDepthStats"/> call
    /// on blocks that do not change the depth distribution.
    /// </summary>
    public bool IsEmpty()
    {
        if (TotalBranchNodesDelta != 0 || TotalBranchChildrenDelta != 0)
            return false;

        for (int i = 0; i < 16; i++)
        {
            if (AccountFullNodes[i]  != 0 || AccountShortNodes[i] != 0 ||
                AccountValueNodes[i] != 0 || AccountNodeBytes[i]  != 0 ||
                StorageFullNodes[i]  != 0 || StorageShortNodes[i] != 0 ||
                StorageValueNodes[i] != 0 || StorageNodeBytes[i]  != 0 ||
                BranchOccupancy[i]   != 0)
                return false;
        }

        return true;
    }

    /// <summary>Zero all arrays and scalars so the instance can be reused for the next diff.</summary>
    public void Clear()
    {
        Array.Clear(AccountFullNodes);
        Array.Clear(AccountShortNodes);
        Array.Clear(AccountValueNodes);
        Array.Clear(AccountNodeBytes);
        Array.Clear(StorageFullNodes);
        Array.Clear(StorageShortNodes);
        Array.Clear(StorageValueNodes);
        Array.Clear(StorageNodeBytes);
        Array.Clear(BranchOccupancy);
        TotalBranchNodesDelta = 0;
        TotalBranchChildrenDelta = 0;
    }
}
