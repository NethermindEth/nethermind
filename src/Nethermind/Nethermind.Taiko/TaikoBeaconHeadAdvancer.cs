// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Taiko;

/// <remarks>
/// On Taiko, the driver (taiko-client) sends one <c>engine_forkchoiceUpdated{HeadBlockHash=tip}</c>
/// inside <c>TriggerBeaconSync</c> and then never re-issues it. Vanilla Nethermind expects a
/// continuous CL drumbeat of FCUs to advance <see cref="IBlockTree.Head"/> as the chain processes
/// blocks during sync. Without that drumbeat, even after BeaconHeadersSync downloads the headers
/// and the executor processes the bodies, <c>Head</c> stays at genesis — the driver then sees
/// <c>eth_syncing.currentBlock=0</c>, decides P2P sync timed out, falls back to event-sync, and
/// starts pushing block 1 by FCU{HeadBlockHash=parentHash=genesis}, which NMC's
/// <c>NewPayloadHandler.cs:147-152</c> rejects as pre-pivot. Deadlock.
///
/// taiko-geth (and alethia-reth) avoid this by advancing the canonical head pointer as part of
/// <c>InsertChain(setHead=true)</c> inside their downloader — the EL takes responsibility for its
/// own canonical head during sync. This class restores the same property in the Nethermind
/// architecture without touching core: it watches for the cached <see cref="IBlockCacheService.HeadBlockHash"/>
/// to become processed, then enqueues every missing ancestor for processing and drives the
/// resulting <c>TryUpdateMainChain</c> sequence to advance <c>Head</c> all the way to that hash.
/// </remarks>
public sealed class TaikoBeaconHeadAdvancer : IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IBlockTree _blockTree;
    private readonly IBlockCacheService _blockCacheService;
    private readonly Lazy<IBlockProcessingQueue> _processingQueue;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    /// <remarks>
    /// <see cref="IBlockProcessingQueue"/> is owned by <c>MainProcessingContext</c>, which
    /// constructs the entire main-processing graph (including <c>BlockInvalidTxExecutor</c>'s
    /// <c>ITxPool</c>) eagerly. Resolving it from this constructor triggers that graph build
    /// before <c>TaikoNethermindApi.TxPool</c> has been wired up by <c>InitializeBlockchainTaiko</c>,
    /// which throws. Use <see cref="Lazy{T}"/> so resolution happens after the main scope has
    /// finished initialization.
    /// </remarks>
    public TaikoBeaconHeadAdvancer(
        IBlockTree blockTree,
        IBlockCacheService blockCacheService,
        Lazy<IBlockProcessingQueue> processingQueue,
        ILogManager logManager)
    {
        _blockTree = blockTree;
        _blockCacheService = blockCacheService;
        _processingQueue = processingQueue;
        _logger = logManager.GetClassLogger<TaikoBeaconHeadAdvancer>();

        _ = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            if (_logger.IsInfo) _logger.Info("[TaikoHeadAdvancer] Started; will periodically advance Head if cached HeadBlockHash is reachable.");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await TryAdvanceOnceAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (_logger.IsWarn) _logger.Warn($"[TaikoHeadAdvancer] iteration failed: {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    await Task.Delay(PollInterval, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* expected */ }
    }

    private async Task TryAdvanceOnceAsync(CancellationToken ct)
    {
        // Take the higher of cached driver target and BestSuggestedBeaconHeader so
        // Head keeps climbing on headers that arrive between driver FCUs.
        Block? target = null;
        ulong? bestNumber = null;

        Hash256? cachedHash = _blockCacheService.HeadBlockHash;
        if (cachedHash is not null && cachedHash != Keccak.Zero)
        {
            Block? cached = _blockTree.FindBlock(cachedHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            if (cached is not null)
            {
                target = cached;
                bestNumber = cached.Number;
            }
        }

        BlockHeader? beaconHead = _blockTree.BestSuggestedBeaconHeader;
        if (beaconHead is not null)
        {
            Hash256? beaconHash = beaconHead.Hash;
            if (beaconHash is not null && (bestNumber is null || beaconHead.Number > bestNumber.Value))
            {
                Block? beaconBlock = _blockTree.FindBlock(beaconHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
                if (beaconBlock is not null)
                {
                    target = beaconBlock;
                    bestNumber = beaconBlock.Number;
                }
            }
        }

        if (target is null) return;

        ulong headNumber = _blockTree.Head?.Number ?? 0UL;
        if (target.Number <= headNumber) return;

        // 2. Enqueue any unprocessed ancestor for processing. The processing queue is FIFO and
        //    BlockchainProcessor walks back to the nearest canonical ancestor automatically, so
        //    enqueuing the highest unprocessed block is enough — but we walk explicitly so that
        //    progress logs show one-by-one advancement.
        ulong firstToEnqueue = headNumber + 1;
        ulong lastToEnqueue = target.Number;
        int enqueued = 0;

        for (ulong n = firstToEnqueue; n <= lastToEnqueue; n++)
        {
            if (ct.IsCancellationRequested) break;

            Block? block = _blockTree.FindBlock(n, BlockTreeLookupOptions.None);
            if (block is null || block.Hash is null)
            {
                if (_logger.IsDebug) _logger.Debug($"[TaikoHeadAdvancer] block {n} not yet in tree; bailing");
                break;
            }

            if (_blockTree.WasProcessed(n, block.Hash))
            {
                continue;
            }

            IBlockProcessingQueue queue = _processingQueue.Value;
            if (queue.Count > 1024)
            {
                // Don't blow up the queue; come back next tick.
                break;
            }

            await queue.Enqueue(block, ProcessingOptions.None | ProcessingOptions.StoreReceipts);
            enqueued++;
        }

        if (enqueued > 0 && _logger.IsInfo)
        {
            _logger.Info($"[TaikoHeadAdvancer] enqueued {enqueued} blocks (Head={headNumber} → target={target.Number}); processor will advance Head.");
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* swallow */ }
        _cts.Dispose();
    }
}
