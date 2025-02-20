// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;

namespace Nethermind.Synchronization.Test;

public class MemorySyncPointers(IBlockTree blockTree, ISyncConfig syncConfig) : ISyncPointers
{
    public long? LowestInsertedBodyNumber { get; set; }
    public long? LowestInsertedReceiptBlockNumber { get; set; }

    public long AncientBodiesBarrier => Math.Max(1, Math.Min(blockTree.SyncPivot.BlockNumber, syncConfig.AncientBodiesBarrier));
    public long AncientReceiptsBarrier => Math.Max(1, Math.Min(blockTree.SyncPivot.BlockNumber, Math.Max(AncientBodiesBarrier, syncConfig.AncientReceiptsBarrier)));
}
