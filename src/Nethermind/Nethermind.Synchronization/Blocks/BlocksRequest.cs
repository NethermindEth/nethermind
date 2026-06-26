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
    public IOwnedReadOnlyList<BlockHeader> BlockAccessListsRequests { get; set; } = IOwnedReadOnlyList<BlockHeader>.Empty;
    public IOwnedReadOnlyList<byte[]?>? BlockAccessLists { get; set; }
    public IOwnedReadOnlyList<BlockHeader> ReceiptsRequests { get; set; } = IOwnedReadOnlyList<BlockHeader>.Empty;
    public IOwnedReadOnlyList<TxReceipt[]?>? Receipts { get; set; }

    public int? NumberOfLatestBlocksToBeIgnored { get; }
    public Task DownloadTask { get; set; } = Task.CompletedTask;
    public int AllCounts => BodiesRequests.Count + BlockAccessListsRequests.Count + ReceiptsRequests.Count;
    private bool _disposed;

    public override string ToString() => $"Blocks Request: {BodiesRequests.Count} Bodies, {BlockAccessListsRequests.Count} Block Access Lists, {ReceiptsRequests.Count} Receipts";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        BodiesRequests.Dispose();
        BlockAccessListsRequests.Dispose();
        ReceiptsRequests.Dispose();
        OwnedBodies?.Dispose();
        BlockAccessLists?.Dispose();
        Receipts?.Dispose();
        DownloadTask?.Dispose();
    }
}
