// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.Trie;

public class GetNodeDataTrieNodeRecovery : TrieNodeRecovery<IReadOnlyList<Keccak>>
{
    public GetNodeDataTrieNodeRecovery(ISyncPeerPool syncPeerPool, ILogManager? logManager) : base(syncPeerPool, logManager)
    {
    }

    public Task<byte[]?> Recover(Keccak keccak)
    {
        using ArrayPoolList<Keccak> request = new(1) { keccak };
        return base.Recover(request);
    }

    protected override string GetMissingNodes(IReadOnlyList<Keccak> request) => string.Join(", ", request);

    protected override bool CanAllocatePeer(ISyncPeer peer) => peer.CanGetNodeData();

    protected override async Task<byte[]?> RecoverRlpFromPeerBase(ISyncPeer peer, IReadOnlyList<Keccak> request, CancellationTokenSource cts)
    {
        byte[][] rlp = await peer.GetNodeData(request, cts.Token);
        if (rlp.Length == 1)
        {
            byte[] recoveredRlp = rlp[0];
            if (ValueKeccak.Compute(recoveredRlp) == request[0])
            {
                return recoveredRlp;
            }

            if (_logger.IsDebug) _logger.Debug($"Recovered RLP from peer {peer} but the hash does not match");
        }

        return null;
    }
}
