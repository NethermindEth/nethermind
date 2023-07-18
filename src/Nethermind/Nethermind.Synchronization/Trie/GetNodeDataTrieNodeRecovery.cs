// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.Trie;

public class GetNodeDataTrieNodeRecovery : TrieNodeRecovery<IReadOnlyList<Keccak>>
{
    public GetNodeDataTrieNodeRecovery(ISyncPeerPool syncPeerPool, IBlockTree blockTree, ILogManager? logManager) : base(syncPeerPool, blockTree, logManager)
    {
    }

    public Task<byte[]?> Recover(Keccak keccak)
    {
        using ArrayPoolList<Keccak> request = new(1) { keccak };
        return base.Recover(request);
    }

    protected override bool CanAllocatePeer(ISyncPeer peer) => base.CanAllocatePeer(peer) && peer.CanGetNodeData();

    protected override async Task<byte[]?> RecoverRlpFromPeerBase(ISyncPeer peer, IReadOnlyList<Keccak> request, CancellationTokenSource cts)
    {
        byte[][] rlp = await peer.GetNodeData(request, cts.Token);
        if (rlp.Length == 1)
        {
            byte[] recoveredRlp = rlp[0];
            if (ValueKeccak.Compute(recoveredRlp) == request[0])
            {
                if (_logger.IsWarn) _logger.Warn($"Recovered RLP from peer {peer} with {recoveredRlp.Length} bytes");
                return recoveredRlp;
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Recovered RLP from peer {peer} but the hash does not match");
            }
        }

        return null;
    }
}
