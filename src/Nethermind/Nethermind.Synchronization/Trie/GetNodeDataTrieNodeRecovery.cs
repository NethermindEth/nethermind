// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Synchronization.Trie;

public class GetNodeDataTrieNodeRecovery : TrieNodeRecovery<IReadOnlyList<Hash256>>
{
    public GetNodeDataTrieNodeRecovery(ISyncPeerPool syncPeerPool, ILogManager? logManager) : base(syncPeerPool, logManager)
    {
    }

    protected override string GetMissingNodes(IReadOnlyList<Hash256> request) => string.Join(", ", request);

    protected override bool CanAllocatePeer(ISyncPeer peer) => peer.CanGetNodeData();

    protected override async Task<byte[]?> RecoverRlpFromPeerBase(ValueHash256 rlpHash, ISyncPeer peer, IReadOnlyList<Hash256> request, CancellationTokenSource cts)
    {
        byte[][] rlp = await peer.GetNodeData(request, cts.Token);
        if (rlp.Length == 1)
        {
            byte[] recoveredRlp = rlp[0];
            if (ValueKeccak.Compute(recoveredRlp) == rlpHash)
            {
                return recoveredRlp;
            }

            if (_logger.IsDebug) _logger.Debug($"Recovered RLP from peer {peer} but the hash does not match");
        }

        return null;
    }
}
