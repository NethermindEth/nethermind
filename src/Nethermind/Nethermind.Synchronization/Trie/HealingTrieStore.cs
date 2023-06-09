// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
    private const int MaxPeersForRecovery = 8;
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
        if (_chainHeadStateProvider is null || _syncPeerPool is null || _peerAllocationStrategyFactory is null) return null;

        if (_logger.IsWarn) _logger.Warn($"Missing trie node {keccak}, trying to recover from network");
        CancellationTokenSource cts = new(_timeout);

            List<KeyRecovery> keyRecoveries = await GenerateKeyRecoveries(keccak, cts);
            try
            {
                return await CheckKeyRecoveriesResults(keyRecoveries, cts);
            }
            finally
            {
                foreach (KeyRecovery keyRecovery in keyRecoveries)
                {
                    _syncPeerPool.Free(keyRecovery.Allocation);
                }
            }
    }

    private static async Task<byte[]?> CheckKeyRecoveriesResults(List<KeyRecovery> keyRecoveries, CancellationTokenSource cts)
    {
        while (keyRecoveries.Count > 0)
        {
            Task<byte[]> task = await Task.WhenAny(keyRecoveries.Select(kr => kr.Task!));
            byte[]? result = await task;
            if (result is null)
            {
                keyRecoveries.RemoveAll(k => k.Task == task);
            }
            else
            {
                cts.Cancel();
                return result;
            }
        }

        return null;
    }

    private async Task<List<KeyRecovery>> GenerateKeyRecoveries(Keccak keccak, CancellationTokenSource cts)
    {
        using ArrayPoolList<StateSyncItem> requestedNodes = new(1) { new StateSyncItem(keccak, null, null, NodeDataType.All) };
        StateSyncBatch request = new(_chainHeadStateProvider!.StateRoot, NodeDataType.All, requestedNodes);
        List<KeyRecovery> keyRecoveries = await AllocatePeers(request);

        using ArrayPoolList<Keccak> requestedHashes = new(1) { keccak };
        foreach (KeyRecovery keyRecovery in keyRecoveries)
        {
            keyRecovery.Task = RecoverRlpFromPeer(keyRecovery.Allocation, requestedHashes, cts);
        }

        return keyRecoveries;
    }

    private async Task<List<KeyRecovery>> AllocatePeers(StateSyncBatch request)
    {
        List<KeyRecovery> syncPeerAllocations = new(MaxPeersForRecovery);

        while (syncPeerAllocations.Count < MaxPeersForRecovery)
        {
            SyncPeerAllocation allocation = await _syncPeerPool!.Allocate(_peerAllocationStrategyFactory!.Create(request), AllocationContexts.State | AllocationContexts.Snap, 1);
            if (allocation.HasPeer)
            {
                syncPeerAllocations.Add(new KeyRecovery { Allocation = allocation });
            }
            else
            {
                break;
            }
        }

        return syncPeerAllocations;
    }

    private async Task<byte[]?> RecoverRlpFromPeer(SyncPeerAllocation allocation, IReadOnlyList<Keccak> requestedHashes, CancellationTokenSource cts)
    {
        try
        {
            byte[][] rlp = await allocation.Current!.SyncPeer.GetNodeData(requestedHashes, cts.Token);
            return rlp.Length == 1 ? rlp[0] : null;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Could not recover {requestedHashes[1]} from {allocation.Current?.SyncPeer}", e);
        }

        return null;
    }

    private class KeyRecovery
    {
        public SyncPeerAllocation Allocation { get; init; } = null!;
        public Task<byte[]?>? Task { get; set; }
    }
}
