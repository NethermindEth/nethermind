// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Trie;

public interface ITrieNodeRecovery<in TRequest>
{
    bool CanRecover => BlockchainProcessor.IsMainProcessingThread;
    Task<byte[]?> Recover(ValueHash256 rlpHash, TRequest request);
}

public abstract class TrieNodeRecovery<TRequest> : ITrieNodeRecovery<TRequest>
{
    private readonly ISyncPeerPool _syncPeerPool;
    protected readonly ILogger _logger;
    private const int MaxPeersForRecovery = 30;

    protected TrieNodeRecovery(ISyncPeerPool syncPeerPool, ILogManager? logManager)
    {
        _syncPeerPool = syncPeerPool;
        _logger = logManager?.GetClassLogger<TrieNodeRecovery<TRequest>>() ?? NullLogger.Instance;
    }

    public async Task<byte[]?> Recover(ValueHash256 rlpHash, TRequest request)
    {
        if (_logger.IsWarn) _logger.Warn($"Missing trie node {GetMissingNodes(request)}, trying to recover from network");
        using CancellationTokenSource cts = new(Timeouts.Eth);
        using ArrayPoolList<Recovery> keyRecoveries = GenerateKeyRecoveries(rlpHash, request, cts);
        return await CheckKeyRecoveriesResults(keyRecoveries, cts);
    }

    protected abstract string GetMissingNodes(TRequest request);

    protected async Task<byte[]?> CheckKeyRecoveriesResults(ArrayPoolList<Recovery> keyRecoveries, CancellationTokenSource cts)
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

    protected ArrayPoolList<Recovery> GenerateKeyRecoveries(in ValueHash256 rlpHash, TRequest request, CancellationTokenSource cts)
    {
        ArrayPoolList<Recovery> keyRecoveries = AllocatePeers();
        if (_logger.IsDebug) _logger.Debug($"Allocated {keyRecoveries.Count} peers (out of {_syncPeerPool!.InitializedPeers.Count()} initialized peers)");
        foreach (Recovery keyRecovery in keyRecoveries)
        {
            keyRecovery.Task = RecoverRlpFromPeer(rlpHash, keyRecovery, request, cts);
        }

        request.TryDispose();
        return keyRecoveries;
    }

    private ArrayPoolList<Recovery> AllocatePeers() =>
        new(MaxPeersForRecovery,
                _syncPeerPool!.InitializedPeers
                    .Select(p => p.SyncPeer)
                    .Where(CanAllocatePeer)
                    .OrderByDescending(p => p.HeadNumber)
                    .Take(MaxPeersForRecovery)
                    .Select(peer => new Recovery { Peer = peer })
            );

    protected abstract bool CanAllocatePeer(ISyncPeer peer);

    private async Task<(Recovery, byte[]?)> RecoverRlpFromPeer(ValueHash256 rlpHash, Recovery recovery, TRequest request, CancellationTokenSource cts)
    {
        ISyncPeer peer = recovery.Peer;

        try
        {
            return (recovery, await RecoverRlpFromPeerBase(rlpHash, peer, request, cts));
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

    protected abstract Task<byte[]?> RecoverRlpFromPeerBase(ValueHash256 rlpHash, ISyncPeer peer, TRequest request, CancellationTokenSource cts);

    protected class Recovery
    {
        public ISyncPeer Peer { get; init; } = null!;
        public Task<(Recovery, byte[]?)>? Task { get; set; }
    }
}
