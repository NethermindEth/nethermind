// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

public interface IForwardHeaderProvider
{
    // Needed to know what is the terminal block so in fast sync, for each
    // header, it calls this.
    void TryUpdateTerminalBlock(BlockHeader currentHeader);
    Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(PeerInfo bestPeer, int skipLastN, int maxHeaders, CancellationToken cancellation);
    void OnNewBestPeer(PeerInfo bestPeer);

    // Can these two be combined?
    void OnBlockAdded(Block currentBlock);
    void IncrementNumber();
}
