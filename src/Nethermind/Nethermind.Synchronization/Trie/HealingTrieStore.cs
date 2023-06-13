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
using Nethermind.Synchronization.StateSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingTrieStore : TrieStore
{
    public static bool Throw = false;

    private const int MaxPeersForRecovery = 8;
    private ISyncPeerPool? _syncPeerPool;
    private IPeerAllocationStrategyFactory<StateSyncBatch>? _peerAllocationStrategyFactory;
    private IReadOnlyStateProvider? _chainHeadStateProvider;
    private readonly ILogger _logger;

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
            if (Throw)
            {
                Throw = false;
                throw new TrieException("Artificial exception");
            }
            return base.LoadRlp(keccak, readFlags);
        }
        catch (TrieException e)
        {
            byte[]? rlp = RecoverRlpFromNetwork(keccak).GetAwaiter().GetResult();
            if (rlp is null) throw new TrieException($"Could not recover {keccak} from network", e);
            if (_logger.IsWarn) _logger.Warn($"Recovered RLP!");
            _keyValueStore.Set(keccak.Bytes, rlp);
            return rlp;
        }
    }

    private async Task<byte[]?> RecoverRlpFromNetwork(Keccak keccak)
    {
        if (_chainHeadStateProvider is null || _syncPeerPool is null || _peerAllocationStrategyFactory is null) return null;

        if (_logger.IsWarn) _logger.Warn($"Missing trie node {keccak}, trying to recover from network");
        using CancellationTokenSource cts = new(Timeouts.Eth);
        List<KeyRecovery> keyRecoveries = GenerateKeyRecoveries(keccak, cts);
        return await CheckKeyRecoveriesResults(keyRecoveries, cts);
    }

    private async Task<byte[]?> CheckKeyRecoveriesResults(List<KeyRecovery> keyRecoveries, CancellationTokenSource cts)
    {
        while (keyRecoveries.Count > 0)
        {
            Task<byte[]> task = await Task.WhenAny(keyRecoveries.Select(kr => kr.Task!));
            byte[]? result = await task;
            int index = keyRecoveries.FindIndex(r => r.Task == task);
            if (result is null)
            {
                _logger.Warn($"Got empty response from peer {keyRecoveries[index].Peer}");
                keyRecoveries.RemoveAt(index);
            }
            else
            {
                _logger.Warn($"Got response from peer {keyRecoveries[index].Peer}");
                cts.Cancel();
                return result;
            }
        }

        return null;
    }

    private List<KeyRecovery> GenerateKeyRecoveries(Keccak keccak, CancellationTokenSource cts)
    {
        using ArrayPoolList<StateSyncItem> requestedNodes = new(1) { new StateSyncItem(keccak, null, null, NodeDataType.All) };
        using ArrayPoolList<Keccak> requestedHashes = new(1) { keccak };
        List<KeyRecovery> keyRecoveries = AllocatePeers();
        if (_logger.IsWarn) _logger.Warn($"Allocated {keyRecoveries.Count} peers (out of {_syncPeerPool!.InitializedPeers.Count()} initialized peers) for recovery of {keccak}");
        foreach (KeyRecovery keyRecovery in keyRecoveries)
        {
            keyRecovery.Task = RecoverRlpFromPeer(keyRecovery.Peer, requestedHashes, cts);
        }

        return keyRecoveries;
    }

    private List<KeyRecovery> AllocatePeers()
    {
        List<KeyRecovery> syncPeerAllocations = new(MaxPeersForRecovery);

        foreach (PeerInfo peer in _syncPeerPool!.InitializedPeers)
        {
            bool canGetNodeData = peer.CanGetNodeData();
            if (canGetNodeData)
            {
                syncPeerAllocations.Add(new KeyRecovery { Peer = peer });
            }
            else
            {
                _logger.Warn($"Peer {peer} can not be allocated with eth{peer.SyncPeer.ProtocolVersion}");
            }

            if (syncPeerAllocations.Count >= MaxPeersForRecovery)
            {
                break;
            }
        }

        return syncPeerAllocations;
    }

    private async Task<byte[]?> RecoverRlpFromPeer(PeerInfo peer, IReadOnlyList<Keccak> requestedHashes, CancellationTokenSource cts)
    {
        try
        {
            byte[][] rlp = await peer.SyncPeer.GetNodeData(requestedHashes, cts.Token);
            if (rlp.Length == 1)
            {
                if (_logger.IsWarn) _logger.Warn($"Recovered RLP from peer {peer} with {rlp[0].Length} bytes");
                return rlp[0];
            }
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsWarn) _logger.Warn($"Cancelled recovering RLP from peer {peer}");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Could not recover {requestedHashes[1]} from {peer.SyncPeer}", e);
        }

        return null;
    }

    private class KeyRecovery
    {
        public PeerInfo Peer { get; init; } = null!;
        public Task<byte[]?>? Task { get; set; }
    }
}
