// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingTrieStore : TrieStore
{
    private ISyncPeerPool? _syncPeerPool;
    private IPeerAllocationStrategyFactory<StateSyncBatch>? _peerAllocationStrategyFactory;
    private IReadOnlyStateProvider? _chainHeadStateProvider;
    private readonly ILogger _logger;
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(1);

    public HealingTrieStore(
        IKeyValueStoreWithBatching? keyValueStore,
        IPruningStrategy? pruningStrategy,
        IPersistenceStrategy? persistenceStrategy,
        ILogManager? logManager)
        : base(keyValueStore, pruningStrategy, persistenceStrategy, logManager)
    {
        _logger = logManager?.GetClassLogger<HealingTrieStore>() ?? NullLogger.Instance;
    }

    public void InitializeNetwork(
        ISyncPeerPool syncPeerPool,
        IPeerAllocationStrategyFactory<StateSyncBatch> peerAllocationStrategyFactory,
        IReadOnlyStateProvider chainHeadStateProvider)
    {
        _syncPeerPool = syncPeerPool;
        _peerAllocationStrategyFactory = peerAllocationStrategyFactory;
        _chainHeadStateProvider = chainHeadStateProvider;
    }

    public override byte[] LoadRlp(Keccak keccak, ReadFlags readFlags = ReadFlags.None)
    {
        try
        {
            return base.LoadRlp(keccak, readFlags);
        }
        catch (TrieException e)
        {
            byte[]? rlp = RecoverRlpFromNetwork(keccak).GetAwaiter().GetResult();
            if (rlp is null) throw new TrieException($"Could not recover {keccak} from network", e);
            _keyValueStore.Set(keccak.Bytes, rlp);
            return rlp;
        }
    }

    private async Task<byte[]?> RecoverRlpFromNetwork(Keccak keccak)
    {
        CancellationTokenSource cts = new(_timeout);
        // We could potentially try to recover from multiple peers here, increasing chances of successful recovery
        return await RecoverRlpFromPeer(keccak, cts);
    }

    private async Task<byte[]?> RecoverRlpFromPeer(Keccak keccak, CancellationTokenSource cts)
    {
        if (_chainHeadStateProvider is null || _syncPeerPool is null || _peerAllocationStrategyFactory is null) return null;

        using ArrayPoolList<StateSyncItem> requestedNodes = new(1) { new StateSyncItem(keccak, null, null, NodeDataType.All) };
        using ArrayPoolList<Keccak> requestedHashes = new(1) { keccak };
        StateSyncBatch request = new(_chainHeadStateProvider.StateRoot, NodeDataType.All, requestedNodes);
        SyncPeerAllocation allocation = await _syncPeerPool.Allocate(_peerAllocationStrategyFactory.Create(request), AllocationContexts.State | AllocationContexts.Snap, 1);
        if (allocation.HasPeer)
        {
            try
            {
                byte[][] rlps = await allocation.Current!.SyncPeer.GetNodeData(requestedHashes, cts.Token);
                return rlps.Length == 1 ? rlps[0] : null;
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Could not recover {keccak} from {allocation.Current?.SyncPeer}", e);
            }
        }

        return null;
    }
}
