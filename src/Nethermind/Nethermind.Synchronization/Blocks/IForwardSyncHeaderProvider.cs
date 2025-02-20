// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

public interface IForwardSyncHeaderProvider
{
    Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(PeerInfo bestPeer, BlocksRequest blocksRequest, int maxHeaders, CancellationToken cancellation);
    void OnNewBestPeer(PeerInfo bestPeer);
    void IncrementNumber();
}
