// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Trie;

public abstract class TrieNodeRecovery<TRequest>
{
    private readonly ISyncPeerPool _syncPeerPool;
    protected readonly ILogger _logger;
    private const int MaxPeersForRecovery = 30;

    protected TrieNodeRecovery(ISyncPeerPool syncPeerPool, ILogManager? logManager)
    {
        _syncPeerPool = syncPeerPool;
        _logger = logManager?.GetClassLogger<TrieNodeRecovery<TRequest>>() ?? NullLogger.Instance;
    }

    public async Task<byte[]?> Recover(TRequest request)
    {
        if (_logger.IsWarn) _logger.Warn($"Missing trie node {GetMissingNodes(request)}, trying to recover from network");
        using CancellationTokenSource cts = new(Timeouts.Eth);
        List<Recovery> keyRecoveries = GenerateKeyRecoveries(request, cts);
        return await CheckKeyRecoveriesResults(keyRecoveries, cts);
    }

    protected abstract string GetMissingNodes(TRequest request);

    protected async Task<byte[]?> CheckKeyRecoveriesResults(List<Recovery> keyRecoveries, CancellationTokenSource cts)
    {

        while (keyRecoveries.Count > 0)
        {
            Task<(Recovery, byte[]?)> task = await Task.WhenAny(keyRecoveries.Select(kr => kr.Task!));
            (Recovery Recovery, byte[]? Data) result = await task;
            if (result.Data is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Got empty response from peer {result.Recovery.Peer}");
                keyRecoveries.Remove(result.Recovery);
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Successfully recovered from peer {result.Recovery.Peer} with {result.Data.Length} bytes!");
                cts.Cancel();
                return result.Data;
            }
        }

        if (_logger.IsWarn) _logger.Warn("Failed to recover missing trie node");

        return null;
    }

    protected List<Recovery> GenerateKeyRecoveries(TRequest requestedHashes, CancellationTokenSource cts)
    {
        List<Recovery> keyRecoveries = AllocatePeers();
        if (_logger.IsDebug) _logger.Debug($"Allocated {keyRecoveries.Count} peers (out of {_syncPeerPool!.InitializedPeers.Count()} initialized peers)");
        foreach (Recovery keyRecovery in keyRecoveries)
        {
            keyRecovery.Task = RecoverRlpFromPeer(keyRecovery, requestedHashes, cts);
        }

        return keyRecoveries;
    }

    private List<Recovery> AllocatePeers()
    {
        List<Recovery> syncPeerAllocations = new(MaxPeersForRecovery);

        foreach (ISyncPeer peer in _syncPeerPool!.InitializedPeers
                     .Select(p => p.SyncPeer)
                     .OrderByDescending(p => p.HeadNumber))
        {
            bool canAllocatePeer = CanAllocatePeer(peer);
            if (canAllocatePeer)
            {
                syncPeerAllocations.Add(new Recovery { Peer = peer });
            }
            else if (_logger.IsTrace) _logger.Trace($"Peer {peer} can not be allocated with eth{peer.ProtocolVersion}");

            if (syncPeerAllocations.Count >= MaxPeersForRecovery)
            {
                break;
            }
        }

        return syncPeerAllocations;
    }

    protected abstract bool CanAllocatePeer(ISyncPeer peer);

    private async Task<(Recovery, byte[]?)> RecoverRlpFromPeer(Recovery recovery, TRequest request, CancellationTokenSource cts)
    {
        ISyncPeer peer = recovery.Peer;

        try
        {
            return (recovery, await RecoverRlpFromPeerBase(peer, request, cts));
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsTrace) _logger.Trace($"Cancelled recovering RLP from peer {peer}");
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Could not recover from {peer}", e);
        }

        return (recovery, null);
    }

    protected abstract Task<byte[]?> RecoverRlpFromPeerBase(ISyncPeer peer, TRequest request, CancellationTokenSource cts);

    protected class Recovery
    {
        public ISyncPeer Peer { get; init; } = null!;
        public Task<(Recovery, byte[]?)>? Task { get; set; }
    }
}
