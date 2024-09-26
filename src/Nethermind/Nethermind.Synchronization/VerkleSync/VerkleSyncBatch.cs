// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Verkle.Tree.Sync;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleSyncBatch
{
    public SubTreeRange? SubTreeRangeRequest { get; set; }

    public SubTreesAndProofs? SubTreeRangeResponse { get; set; }

    public LeafToRefreshRequest? LeafToRefreshRequest { get; set; }
    public byte[][]? LeafToRefreshResponse { get; set; }

    public override string ToString()
    {
        if (SubTreeRangeRequest is not null)
        {
            return SubTreeRangeRequest!.ToString();
        }
        else if (LeafToRefreshRequest is not null)
        {
            return LeafToRefreshRequest!.ToString();
        }
        else
        {
            return "Empty snap sync batch";
        }
    }
}
