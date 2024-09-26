// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.History.V1;

public class ChangeSet
{
    public ChangeSet(long blockNumber, VerkleMemoryDb diffLayer)
    {
        BlockNumber = blockNumber;
        DiffLayer = diffLayer;
    }

    public long BlockNumber { get; }
    public VerkleMemoryDb DiffLayer { get; }
}

public class BatchChangeSet
{
    public BatchChangeSet(long fromBlockNumber, long toBlockNumber, VerkleMemoryDb diffLayer)
    {
        FromBlockNumber = fromBlockNumber;
        ToBlockNumber = toBlockNumber;
        DiffLayer = diffLayer;
    }

    public long FromBlockNumber { get; }
    public long ToBlockNumber { get; }
    public VerkleMemoryDb DiffLayer { get; }
}
