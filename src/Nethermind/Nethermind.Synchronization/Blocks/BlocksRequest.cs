// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.Blocks;

public sealed class BlocksRequest : IDisposable
{
    public IOwnedReadOnlyList<BlockHeader> BodiesRequests { get; set; } = IOwnedReadOnlyList<BlockHeader>.Empty;
    public OwnedBlockBodies? OwnedBodies { get; set; }

    /// <summary>Hashes per EIP-8512 <c>GetDependencyProofs</c>, an implementation-defined limit serving peers enforce too.</summary>
    public const int MaxDependencyProofsPerRequest = 64;

    public IOwnedReadOnlyList<BlockHeader> DependencyProofsRequests { get; set; } = IOwnedReadOnlyList<BlockHeader>.Empty;

    /// <summary>The proofs served for <see cref="DependencyProofsRequests"/>, in order; possibly fewer, an empty one unavailable.</summary>
    public IOwnedReadOnlyList<byte[]>? DependencyProofs { get; set; }
    public IOwnedReadOnlyList<BlockHeader> BlockAccessListsRequests { get; set; } = IOwnedReadOnlyList<BlockHeader>.Empty;
    public IOwnedReadOnlyList<byte[]?>? BlockAccessLists { get; set; }
    public IOwnedReadOnlyList<BlockHeader> ReceiptsRequests { get; set; } = IOwnedReadOnlyList<BlockHeader>.Empty;

    /// <summary>
    /// Per block in <see cref="ReceiptsRequests"/>, its transaction count, which bounds the receipts a peer may return for it.
    /// </summary>
    public ArrayPoolList<int>? ExpectedReceiptCounts { get; set; }

    public IOwnedReadOnlyList<TxReceipt[]?>? Receipts { get; set; }

    public int? NumberOfLatestBlocksToBeIgnored { get; }
    public Task DownloadTask { get; set; }
    public int AllCounts => BodiesRequests.Count + BlockAccessListsRequests.Count + DependencyProofsRequests.Count + ReceiptsRequests.Count;
    private bool _disposed;

    public override string ToString() => $"Blocks Request: {BodiesRequests.Count} Bodies, {BlockAccessListsRequests.Count} Block Access Lists, {DependencyProofsRequests.Count} Dependency Proofs, {ReceiptsRequests.Count} Receipts";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        BodiesRequests.Dispose();
        BlockAccessListsRequests.Dispose();
        DependencyProofsRequests.Dispose();
        ReceiptsRequests.Dispose();
        ExpectedReceiptCounts?.Dispose();
        OwnedBodies?.Dispose();
        BlockAccessLists?.Dispose();
        DependencyProofs?.Dispose();
        Receipts?.Dispose();
        DownloadTask?.Dispose();
    }
}
