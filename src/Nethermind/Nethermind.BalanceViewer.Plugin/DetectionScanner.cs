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
    // Transfer(address indexed from, address indexed to, uint256 value)
    private static readonly Hash256 TransferTopic = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");

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
            if (hi <= 0) { Persist(chainId, account, existing?.Contracts, 0, head, complete: true); _active.TryRemove(key, out _); return Task.CompletedTask; }
            long lo = Math.Max(0, hi - ChunkBlocks + 1);

            HashSet<string> contracts = existing is null ? [] : [.. existing.Contracts];
            Hash256 accountTopic = ToTopic(account);
            try
            {
                CollectChunk(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferTopic), new SpecificTopic(accountTopic)), contracts, token);
                CollectChunk(lo, hi, new SequenceTopicsFilter(new SpecificTopic(TransferTopic), AnyTopic.Instance, new SpecificTopic(accountTopic)), contracts, token);
            }
            catch (OperationCanceledException)
            {
                // pre-empted by block processing (or deadline) — keep any contracts found, don't advance the
                // cursor, and retry this range on a later scheduling slot
                Persist(chainId, account, contracts, priorScannedFrom, head, complete: false);
                Schedule(req);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                // reached the bottom of retained history (pruned/unavailable receipts)
                if (_logger.IsDebug) _logger.Debug($"Token detection reached retained-history floor for {account} at block {lo}: {e.Message}");
                Persist(chainId, account, contracts, 0, head, complete: true);
                _active.TryRemove(key, out _);
                return Task.CompletedTask;
            }

            bool complete = lo <= 0 || contracts.Count >= MaxContracts;
            Persist(chainId, account, contracts, complete ? 0 : lo, head, complete);
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

    private void CollectChunk(long lo, long hi, TopicsFilter topics, HashSet<string> contracts, CancellationToken token)
    {
        LogFilter filter = new(0, new BlockParameter((ulong)lo), new BlockParameter((ulong)hi), AddressFilter.AnyAddress, topics)
        {
            UseIndex = false // use the bloom-backed finder; don't require the optional log index to be enabled
        };
        foreach (FilterLog log in logFinder.FindLogs(filter, token))
        {
            // ERC-20 Transfer carries exactly 3 topics (ERC-721's are fully indexed -> 4)
            if (log.Topics.Length == 3) contracts.Add(log.Address.ToString());
        }
    }

    private void Persist(long chainId, Address account, IEnumerable<string>? contracts, long scannedFrom, long head, bool complete) =>
        cache.Put(chainId, account.ToString(), new DetectionEntry(
            contracts is null ? [] : [.. contracts], scannedFrom, head, complete, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    private static Hash256 ToTopic(Address account)
    {
        byte[] topic = new byte[32];
        account.Bytes.CopyTo(topic.AsSpan(12));
        return new Hash256(topic);
    }
}
