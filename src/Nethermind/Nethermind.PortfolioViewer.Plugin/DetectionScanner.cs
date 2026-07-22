// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;

namespace Nethermind.PortfolioViewer.Plugin;

/// <summary>Runs token-detection history scans for accounts, in-process, on this node's chain.</summary>
public interface IDetectionScanner
{
    /// <summary>Starts (or resumes) a background scan for the account; a no-op if already complete or running.</summary>
    void RequestScan(long chainId, Address account);
}

/// <summary>Discovers the token/NFT contracts an account transferred, from this node's log index.</summary>
/// <remarks>
/// Runs through the <see cref="IBackgroundTaskScheduler"/> (yields to block processing), split into block
/// chunks chained until retained history is covered, so a deep scan can't affect validator performance.
/// </remarks>
public sealed class DetectionScanner(
    IBackgroundTaskScheduler scheduler, ILogFinder logFinder, IBlockFinder blockFinder, IDetectionCache cache, ILogManager logManager)
    : IDetectionScanner
{
    // ERC-20/ERC-721 Transfer, and ERC-1155 TransferSingle/TransferBatch event signatures
    private static readonly Hash256 TransferTopic = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");
    private static readonly Hash256 TransferSingleTopic = new("0xc3d58168c5ae7397731d063d5bbf3d657854427343f4c083240f7aacaa2d0f62");
    private static readonly Hash256 TransferBatchTopic = new("0x4a39dc06d4c0dbc64b70af90fd698a233a518aa5d07e595d983b8c0526c8f7fb");

    // Per-account chunk size adapts: grows after a chunk completes, halves when block processing pre-empts one,
    // so busy accounts settle on a size that fits an inter-block gap.
    private const int BaseChunkBlocks = 5_000;
    private const int MinChunkBlocks = 250;
    private const int MaxChunkBlocks = 250_000;
    private const int MaxContractsPerScan = 2_000;  // combined erc20+nft cap that ends a scan (distinct from the cache's per-list cap)
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger _logger = logManager.GetClassLogger<DetectionScanner>();
    private readonly ConcurrentDictionary<string, int> _active = new(); // key -> current adaptive chunk size

    private int CurrentChunk(string key) => _active.TryGetValue(key, out int c) ? c : BaseChunkBlocks;
    private void Grow(string key) => _active.AddOrUpdate(key, BaseChunkBlocks, static (_, c) => Math.Min(c * 2, MaxChunkBlocks));
    private void Shrink(string key) => _active.AddOrUpdate(key, MinChunkBlocks, static (_, c) => Math.Max(c / 2, MinChunkBlocks));

    private readonly record struct DetectRequest(long ChainId, Address Account);

    private static string Key(long chainId, Address account) => chainId + ":" + account.ToString().ToLowerInvariant();

    public void RequestScan(long chainId, Address account)
    {
        long head = (long)(blockFinder.Head?.Number ?? 0);
        DetectionEntry? entry = cache.Get(chainId, account.ToString());
        // skip only if history is fully covered AND no new blocks arrived; else re-scan the forward gap
        if (entry is { Complete: true } && entry.Head >= head) return;
        if (!_active.TryAdd(Key(chainId, account), BaseChunkBlocks)) return; // a chunk chain is already running for this account
        Schedule(new DetectRequest(chainId, account));
    }

    private void Schedule(DetectRequest req)
    {
        if (!scheduler.TryScheduleTask(req, RunChunkAsync, ChunkTimeout, "portfolio-viewer-detection"))
        {
            // scheduler queue is full — give up this chain; the client re-triggers on its next poll
            _active.TryRemove(Key(req.ChainId, req.Account), out _);
        }
    }

    private Task RunChunkAsync(DetectRequest req, CancellationToken token)
    {
        (long chainId, Address account) = req;
        string key = Key(chainId, account);
        try
        {
            DetectionEntry? existing = cache.Get(chainId, account.ToString());
            long curHead = (long)(blockFinder.Head?.Number ?? 0);
            int chunk = CurrentChunk(key);

            // Forward phase: cover blocks since the last scan first, so freshly-received tokens surface before
            // the downward history walk.
            if (existing is not null && curHead > existing.Head)
            {
                long flo = existing.Head + 1;
                long fhi = Math.Min(curHead, flo + chunk - 1);
                HashSet<string> fErc20 = [.. existing.Contracts];
                HashSet<string> fNfts = [.. existing.NftContracts];
                try
                {
                    CollectAll(flo, fhi, account, fErc20, fNfts, token);
                }
                catch (OperationCanceledException)
                {
                    Shrink(key);
                    Persist(chainId, account, existing.Contracts, existing.NftContracts, existing.ScannedFrom, existing.Head, existing.Complete);
                    Schedule(req);
                    return Task.CompletedTask;
                }
                Grow(key);
                Persist(chainId, account, fErc20, fNfts, existing.ScannedFrom, fhi, existing.Complete);
                Schedule(req); // continue: remaining forward gap, then any downward history
                return Task.CompletedTask;
            }
            if (existing?.Complete == true) { _active.TryRemove(key, out _); return Task.CompletedTask; }

            long head = existing?.Head ?? curHead;
            if (head <= 0) { _active.TryRemove(key, out _); return Task.CompletedTask; } // node not ready; client re-triggers

            long priorScannedFrom = existing is { ScannedFrom: > 0 } ? existing.ScannedFrom : head + 1;
            long hi = priorScannedFrom - 1;
            if (hi <= 0) { Persist(chainId, account, existing?.Contracts, existing?.NftContracts, 0, head, complete: true); _active.TryRemove(key, out _); return Task.CompletedTask; }
            long lo = Math.Max(0, hi - chunk + 1);

            HashSet<string> erc20 = existing is null ? [] : [.. existing.Contracts];
            HashSet<string> nfts = existing is null ? [] : [.. existing.NftContracts];
            try
            {
                CollectAll(lo, hi, account, erc20, nfts, token);
            }
            catch (OperationCanceledException)
            {
                // pre-empted by block processing — keep found contracts, don't advance the cursor, shrink, retry
                Shrink(key);
                Persist(chainId, account, erc20, nfts, priorScannedFrom, head, complete: false);
                Schedule(req);
                return Task.CompletedTask;
            }
            catch (ResourceNotFoundException e)
            {
                // reached the bottom of retained history (pruned/unavailable receipts)
                if (_logger.IsDebug) _logger.Debug($"Token detection reached retained-history floor for {account} at block {lo}: {e.Message}");
                Persist(chainId, account, erc20, nfts, 0, head, complete: true);
                _active.TryRemove(key, out _);
                return Task.CompletedTask;
            }

            bool complete = lo <= 0 || erc20.Count + nfts.Count >= MaxContractsPerScan;
            Persist(chainId, account, erc20, nfts, complete ? 0 : lo, head, complete);
            if (complete) _active.TryRemove(key, out _);
            else { Grow(key); Schedule(req); } // chunk finished within the gap — go wider next time
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Token detection chunk failed for {account}: {e.Message}");
            _active.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    // Scans [lo..hi] for the account as Transfer sender/receiver and ERC-1155 receiver.
    private void CollectAll(long lo, long hi, Address account, HashSet<string> erc20, HashSet<string> nfts, CancellationToken token)
    {
        Hash256 accountTopic = ToTopic(account);
        // Transfer (ERC-20/721): from = topic1, to = topic2
        Collect(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferTopic), new SpecificTopic(accountTopic)), erc20, nfts, token);
        Collect(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferTopic), AnyTopic.Instance, new SpecificTopic(accountTopic)), erc20, nfts, token);
        // ERC-1155 received: to = topic3
        Collect(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferSingleTopic), AnyTopic.Instance, AnyTopic.Instance, new SpecificTopic(accountTopic)), null, nfts, token);
        Collect(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferBatchTopic), AnyTopic.Instance, AnyTopic.Instance, new SpecificTopic(accountTopic)), null, nfts, token);
    }

    // erc20 non-null (Transfer scans): 3-topic logs are ERC-20, 4-topic are ERC-721. erc20 null (ERC-1155): all NFT.
    private void Collect(long lo, long hi, TopicsFilter topics, HashSet<string>? erc20, HashSet<string> nfts, CancellationToken token)
    {
        LogFilter filter = new(0, new BlockParameter((ulong)lo), new BlockParameter((ulong)hi), AddressFilter.AnyAddress, topics)
        {
            UseIndex = true // use the per-address log index when enabled, so deep scans skip ranges with no logs
        };
        foreach (FilterLog log in logFinder.FindLogs(filter, token))
        {
            if (erc20 is not null && log.Topics.Length == 3) erc20.Add(log.Address.ToString());
            else nfts.Add(log.Address.ToString());
        }
    }

    private void Persist(long chainId, Address account, IEnumerable<string>? contracts, IEnumerable<string>? nftContracts, long scannedFrom, long head, bool complete) =>
        cache.Put(chainId, account.ToString(), new DetectionEntry(
            contracts is null ? [] : [.. contracts], nftContracts is null ? [] : [.. nftContracts],
            scannedFrom, head, complete, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    private static Hash256 ToTopic(Address account)
    {
        byte[] topic = new byte[32];
        account.Bytes.CopyTo(topic.AsSpan(12));
        return new Hash256(topic);
    }
}
