// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Synchronization;

public class Pivot : IPivot
{
    public Pivot(IBlockTree blockTree)
    {
        PivotNumber = blockTree.SyncPivot.BlockNumber;
        PivotHash = blockTree.SyncPivot.BlockHash;
        PivotDestinationNumber = 0L;
    }

    public long PivotNumber { get; }

    public Hash256? PivotHash { get; }

    public Hash256? PivotParentHash => null;

    public long PivotDestinationNumber { get; }
}
