// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Snap;
using Nethermind.Stats.Model;

namespace Nethermind.Synchronization.Test.ParallelSync;

public class BaseSyncPeerMock : ISyncPeer, ISnapSyncPeer
{
    public virtual PublicKey Id { get; } = null!;
    public long HeadNumber { get; set; }
    public virtual string ClientId { get; set; } = null!;
    public virtual void SendNewTransactions(IEnumerable<Transaction> txs, bool sendFullTx)
    {
        throw new System.NotImplementedException();
    }

    public void RegisterSatelliteProtocol<T>(string protocol, T protocolHandler) where T : class
    {
        throw new System.NotImplementedException();
    }

    public virtual bool TryGetSatelliteProtocol<T>(string protocol, out T protocolHandler) where T : class
    {
        throw new System.NotImplementedException();
    }

    public virtual Node Node { get; set; } = null!;
    public virtual string Name { get; } = null!;
    public Hash256 HeadHash { get; set; } = null!;
    public virtual UInt256? TotalDifficulty { get; set; }
    public bool IsInitialized { get; set; }
    public bool IsPriority { get; set; }
    public virtual byte ProtocolVersion { get; }
    public string ProtocolCode { get; } = null!;
    public virtual void Disconnect(DisconnectReason reason, string details)
    {
    }

    public virtual Task<OwnedBlockBodies> GetBlockBodies(IReadOnlyList<Hash256> blockHashes, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public virtual Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(long number, int maxBlocks, int skip, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(Hash256 startHash, int maxBlocks, int skip, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public virtual Task<BlockHeader?> GetHeadBlockHeader(Hash256? hash, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public virtual void NotifyOfNewBlock(Block block, SendBlockMode mode)
    {
        throw new System.NotImplementedException();
    }

    public Task<IOwnedReadOnlyList<TxReceipt[]?>> GetReceipts(IReadOnlyList<Hash256> blockHash, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public virtual Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public Task<SlotsAndProofs> GetStorageRange(StorageRange range, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public virtual Task<IOwnedReadOnlyList<byte[]>> GetByteCodes(IReadOnlyList<ValueHash256> codeHashes, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public virtual Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }

    public virtual Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token)
    {
        throw new System.NotImplementedException();
    }
}
