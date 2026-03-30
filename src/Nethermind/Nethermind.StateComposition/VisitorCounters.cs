// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

/// <summary>
/// Mutable counters for ThreadLocal usage in StateCompositionVisitor.
/// Each worker thread gets its own instance — no locking needed.
/// Aggregate via MergeFrom() after traversal completes.
/// </summary>
public sealed class VisitorCounters
{
    public long AccountsTotal;
    public long ContractsTotal;
    public long ContractsWithStorage;
    public long StorageSlotsTotal;
    public long TotalCodeSize;

    public long AccountNodeBytes;
    public long StorageNodeBytes;

    public long AccountBranches;
    public long AccountExtensions;
    public long AccountLeaves;

    public long StorageBranches;
    public long StorageExtensions;
    public long StorageLeaves;

    public long TotalBranchChildren;
    public long TotalBranchNodes;

    public readonly DepthCounter[] AccountDepths = new DepthCounter[16];
    public readonly DepthCounter[] StorageDepths = new DepthCounter[16];

    public void MergeFrom(VisitorCounters other)
    {
        AccountsTotal += other.AccountsTotal;
        ContractsTotal += other.ContractsTotal;
        ContractsWithStorage += other.ContractsWithStorage;
        StorageSlotsTotal += other.StorageSlotsTotal;
        TotalCodeSize += other.TotalCodeSize;

        AccountNodeBytes += other.AccountNodeBytes;
        StorageNodeBytes += other.StorageNodeBytes;

        AccountBranches += other.AccountBranches;
        AccountExtensions += other.AccountExtensions;
        AccountLeaves += other.AccountLeaves;

        StorageBranches += other.StorageBranches;
        StorageExtensions += other.StorageExtensions;
        StorageLeaves += other.StorageLeaves;

        TotalBranchChildren += other.TotalBranchChildren;
        TotalBranchNodes += other.TotalBranchNodes;

        for (int i = 0; i < 16; i++)
        {
            AccountDepths[i].Branches += other.AccountDepths[i].Branches;
            AccountDepths[i].Extensions += other.AccountDepths[i].Extensions;
            AccountDepths[i].Leaves += other.AccountDepths[i].Leaves;
            AccountDepths[i].ByteSize += other.AccountDepths[i].ByteSize;

            StorageDepths[i].Branches += other.StorageDepths[i].Branches;
            StorageDepths[i].Extensions += other.StorageDepths[i].Extensions;
            StorageDepths[i].Leaves += other.StorageDepths[i].Leaves;
            StorageDepths[i].ByteSize += other.StorageDepths[i].ByteSize;
        }
    }
}
