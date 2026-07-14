// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
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
    /// <summary>Queues (or resumes) a background scan for the account; a no-op if already complete or queued.</summary>
    void RequestScan(long chainId, Address account);
}

/// <summary>
/// Discovers the ERC-20 contracts an account has transferred to/from by scanning the node's own
/// log index in-process, and writes the candidate set to <see cref="IDetectionCache"/>.
/// </summary>
/// <remarks>
/// Uses <see cref="ILogFinder"/> directly (bloom-index backed) rather than JSON-RPC <c>eth_getLogs</c>,
/// so it is not subject to the RPC block-range / result-count caps and avoids thousands of HTTP
/// round-trips. Scans run one-at-a-time on a single background worker, in bounded block chunks with a
/// pause between each and honouring a cancellation token, so a deep historical scan cannot starve the
/// node's block-processing / attestation duties. Only this node's data is read; nothing leaves the machine.
/// </remarks>
public sealed class DetectionScanner : IDetectionScanner, IAsyncDisposable
{
    // Transfer(address indexed from, address indexed to, uint256 value)
    private static readonly Hash256 TransferTopic = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");

    private const int ChunkBlocks = 50_000;  // blocks per in-process log query (no RPC cap applies here)
    private const int PaceDelayMs = 50;      // pause between chunks so validator duties aren't starved
    private const int MaxContracts = 2_000;  // safety cap on distinct contracts discovered per account

    private readonly ILogFinder _logFinder;
    private readonly IBlockFinder _blockFinder;
    private readonly IDetectionCache _cache;
    private readonly ILogger _logger;
    private readonly Channel<(long ChainId, Address Account)> _queue =
        Channel.CreateUnbounded<(long, Address)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public DetectionScanner(ILogFinder logFinder, IBlockFinder blockFinder, IDetectionCache cache, ILogManager logManager)
    {
        _logFinder = logFinder;
        _blockFinder = blockFinder;
        _cache = cache;
        _logger = logManager.GetClassLogger<DetectionScanner>();
        _worker = Task.Run(() => WorkerLoop(_cts.Token));
    }

    public void RequestScan(long chainId, Address account)
    {
        if (_cache.Get(chainId, account.ToString())?.Complete == true) return; // already fully scanned
        string key = chainId + ":" + account.ToString().ToLowerInvariant();
        if (!_queued.TryAdd(key, 0)) return; // already queued or running
        if (!_queue.Writer.TryWrite((chainId, account))) _queued.TryRemove(key, out _);
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        try
        {
            await foreach ((long chainId, Address account) in _queue.Reader.ReadAllAsync(ct))
            {
                string key = chainId + ":" + account.ToString().ToLowerInvariant();
                try
                {
                    await ScanAsync(chainId, account, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e)
                {
                    if (_logger.IsWarn) _logger.Warn($"Token detection scan failed for {account}: {e.Message}");
                }
                finally { _queued.TryRemove(key, out _); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ScanAsync(long chainId, Address account, CancellationToken ct)
    {
        ulong? headNumber = _blockFinder.Head?.Number;
        if (headNumber is null or 0) return; // node not ready / still at genesis
        long head = (long)headNumber.Value;

        DetectionEntry? existing = _cache.Get(chainId, account.ToString());
        HashSet<string> contracts = existing is null ? [] : [.. existing.Contracts];
        // resume just below the lowest previously scanned block, else start at head
        long cursor = existing is { Complete: false, ScannedFrom: > 0 } ? existing.ScannedFrom - 1 : head;

        Hash256 accountTopic = ToTopic(account);
        SequenceTopicsFilter outgoing = new(new SpecificTopic(TransferTopic), new SpecificTopic(accountTopic));
        SequenceTopicsFilter incoming = new(new SpecificTopic(TransferTopic), AnyTopic.Instance, new SpecificTopic(accountTopic));

        bool complete = false;
        while (cursor > 0 && !ct.IsCancellationRequested)
        {
            long hi = cursor;
            long lo = Math.Max(0, hi - ChunkBlocks + 1);
            try
            {
                CollectChunk(lo, hi, outgoing, contracts, ct);
                CollectChunk(lo, hi, incoming, contracts, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                // reached the bottom of retained history (pruned/unavailable receipts)
                if (_logger.IsDebug) _logger.Debug($"Token detection reached retained-history floor for {account} at block {lo}: {e.Message}");
                complete = true;
                break;
            }
            cursor = lo - 1;
            complete = cursor <= 0 || contracts.Count >= MaxContracts;
            Persist(chainId, account, contracts, complete ? 0 : cursor + 1, head, complete);
            if (complete) return;
            await Task.Delay(PaceDelayMs, ct);
        }

        Persist(chainId, account, contracts, complete ? 0 : Math.Max(0, cursor + 1), head, complete || cursor <= 0);
    }

    private void CollectChunk(long lo, long hi, TopicsFilter topics, HashSet<string> contracts, CancellationToken ct)
    {
        LogFilter filter = new(0, new BlockParameter((ulong)lo), new BlockParameter((ulong)hi), AddressFilter.AnyAddress, topics)
        {
            UseIndex = false // use the bloom-backed finder; don't require the optional log index to be enabled
        };
        foreach (FilterLog log in _logFinder.FindLogs(filter, ct))
        {
            // ERC-20 Transfer carries exactly 3 topics (ERC-721's are fully indexed -> 4)
            if (log.Topics.Length == 3) contracts.Add(log.Address.ToString());
        }
    }

    private void Persist(long chainId, Address account, HashSet<string> contracts, long scannedFrom, long head, bool complete) =>
        _cache.Put(chainId, account.ToString(), new DetectionEntry(
            [.. contracts], scannedFrom, head, complete, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    private static Hash256 ToTopic(Address account)
    {
        byte[] topic = new byte[32];
        account.Bytes.CopyTo(topic.AsSpan(12));
        return new Hash256(topic);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync();
        try { await _worker; } catch (Exception) { /* shutting down */ }
        _cts.Dispose();
    }
}
