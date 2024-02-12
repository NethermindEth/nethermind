// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.Utils;

public class InsertBatchCompletedV1(long blockNumber, SortedVerkleMemoryDb forwardDiff, VerkleMemoryDb? reverseDiff)
    : EventArgs
{
    public VerkleMemoryDb? ReverseDiff { get; } = reverseDiff;
    public SortedVerkleMemoryDb ForwardDiff { get; } = forwardDiff;
    public long BlockNumber { get; } = blockNumber;
}

public class InsertBatchCompletedV2(long blockNumber, LeafStoreInterface leafTable) : EventArgs
{
    public LeafStoreInterface LeafTable { get; } = leafTable;
    public long BlockNumber { get; } = blockNumber;
}
