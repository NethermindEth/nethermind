// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;

namespace Nethermind.BalanceViewer.Plugin;

/// <summary>Runs token-detection history scans for accounts, in-process, on this node's chain.</summary>
public interface IDetectionScanner
{
    /// <summary>Starts (or resumes) a background scan for the account; a no-op if already complete or running.</summary>
    void RequestScan(long chainId, Address account);
}

/// <summary>
/// Discovers the ERC-20 contracts an account has transferred to/from by scanning the node's own log
/// index in-process, and writes the candidate set to <see cref="IDetectionCache"/>.
/// </summary>
/// <remarks>
/// Work runs through the node's <see cref="IBackgroundTaskScheduler"/>: at BelowNormal thread priority,
/// paused entirely while a block is being processed, with a token cancelled when a block arrives — so a
/// deep historical scan yields to block-processing / attestation and cannot affect validator performance.
/// The scan is split into small block chunks, one scheduled task each, chained until the retained history
/// is covered; a chunk pre-empted by block processing is retried (results accumulate in the cache). Uses
/// <see cref="ILogFinder"/> directly (bloom-index backed), so it is not subject to the JSON-RPC
/// block-range / result-count caps. Only this node's data is read.
/// </remarks>
public sealed class DetectionScanner(
    IBackgroundTaskScheduler scheduler, ILogFinder logFinder, IBlockFinder blockFinder, IDetectionCache cache, ILogManager logManager)
    : IDetectionScanner
{
    // Transfer(address indexed from, address indexed to, uint256 value) — ERC-20 (3 topics) and ERC-721 (4 topics)
    private static readonly Hash256 TransferTopic = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");
    // ERC-1155 TransferSingle(operator, from indexed, to indexed, id, value) and TransferBatch (same indexed args)
    private static readonly Hash256 TransferSingleTopic = new("0xc3d58168c5ae7397731d063d5bbf3d657854427343f4c083240f7aacaa2d0f62");
    private static readonly Hash256 TransferBatchTopic = new("0x4a39dc06d4c0dbc64b70af90fd698a233a518aa5d07e595d983b8c0526c8f7fb");

    private const int ChunkBlocks = 5_000;   // small chunks so each scheduled task finishes within an inter-block gap
    private const int MaxContracts = 2_000;  // safety cap on distinct contracts discovered per account
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger _logger = logManager.GetClassLogger<DetectionScanner>();
    private readonly ConcurrentDictionary<string, byte> _active = new();

    private readonly record struct DetectRequest(long ChainId, Address Account);

    private static string Key(long chainId, Address account) => chainId + ":" + account.ToString().ToLowerInvariant();

    public void RequestScan(long chainId, Address account)
    {
        if (cache.Get(chainId, account.ToString())?.Complete == true) return;
        if (!_active.TryAdd(Key(chainId, account), 0)) return; // a chunk chain is already running for this account
        Schedule(new DetectRequest(chainId, account));
    }

    private void Schedule(DetectRequest req)
    {
        if (!scheduler.TryScheduleTask(req, RunChunkAsync, ChunkTimeout, "balance-viewer-detection"))
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
            if (existing?.Complete == true) { _active.TryRemove(key, out _); return Task.CompletedTask; }

            long head = existing?.Head ?? (long)(blockFinder.Head?.Number ?? 0);
            if (head <= 0) { _active.TryRemove(key, out _); return Task.CompletedTask; } // node not ready; client re-triggers

            long priorScannedFrom = existing is { ScannedFrom: > 0 } ? existing.ScannedFrom : head + 1;
            long hi = priorScannedFrom - 1;
            if (hi <= 0) { Persist(chainId, account, existing?.Contracts, existing?.NftContracts, 0, head, complete: true); _active.TryRemove(key, out _); return Task.CompletedTask; }
            long lo = Math.Max(0, hi - ChunkBlocks + 1);

            HashSet<string> erc20 = existing is null ? [] : [.. existing.Contracts];
            HashSet<string> nfts = existing is null ? [] : [.. existing.NftContracts];
            Hash256 accountTopic = ToTopic(account);
            try
            {
                // ERC-20 (3-topic) and ERC-721 (4-topic) share the Transfer event; from = topic1, to = topic2
                Collect(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferTopic), new SpecificTopic(accountTopic)), erc20, nfts, token);
                Collect(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferTopic), AnyTopic.Instance, new SpecificTopic(accountTopic)), erc20, nfts, token);
                // ERC-1155 received: TransferSingle/TransferBatch with to = topic3 (operator = topic1, from = topic2)
                Collect(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferSingleTopic), AnyTopic.Instance, AnyTopic.Instance, new SpecificTopic(accountTopic)), null, nfts, token);
                Collect(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferBatchTopic), AnyTopic.Instance, AnyTopic.Instance, new SpecificTopic(accountTopic)), null, nfts, token);
            }
            catch (OperationCanceledException)
            {
                // pre-empted by block processing (or deadline) — keep any contracts found, don't advance the
                // cursor, and retry this range on a later scheduling slot
                Persist(chainId, account, erc20, nfts, priorScannedFrom, head, complete: false);
                Schedule(req);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                // reached the bottom of retained history (pruned/unavailable receipts)
                if (_logger.IsDebug) _logger.Debug($"Token detection reached retained-history floor for {account} at block {lo}: {e.Message}");
                Persist(chainId, account, erc20, nfts, 0, head, complete: true);
                _active.TryRemove(key, out _);
                return Task.CompletedTask;
            }

            bool complete = lo <= 0 || erc20.Count + nfts.Count >= MaxContracts;
            Persist(chainId, account, erc20, nfts, complete ? 0 : lo, head, complete);
            if (complete) _active.TryRemove(key, out _);
            else Schedule(req);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Token detection chunk failed for {account}: {e.Message}");
            _active.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    // Collects emitting-contract addresses. When erc20 is provided (Transfer scans), 3-topic logs are
    // ERC-20 and 4-topic logs are ERC-721; when erc20 is null (ERC-1155 event scans), everything is an NFT.
    private void Collect(long lo, long hi, TopicsFilter topics, HashSet<string>? erc20, HashSet<string> nfts, CancellationToken token)
    {
        LogFilter filter = new(0, new BlockParameter((ulong)lo), new BlockParameter((ulong)hi), AddressFilter.AnyAddress, topics)
        {
            UseIndex = false // use the bloom-backed finder; don't require the optional log index to be enabled
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
