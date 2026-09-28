// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Eez.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Follower;

/// <summary>
/// Follows the rollup from L1: scans L1 for the batches that settled it, derives every block they settle and moves
/// the safe head to what L1 stores, and the finalized head to what a finalized L1 block stores. An L1 reorganization
/// sends it back to the last settlement that survived. A settlement the local chain cannot reproduce stops the node,
/// since serving blocks L1 disagrees with would be worse than serving none.
/// </summary>
public sealed class EezFollower(
    IEezL1Api l1,
    L1BatchScanner scanner,
    BatchReconciler reconciler,
    ResumePointFinder resumePoints,
    IUnsafeHeadSource unsafeHead,
    IBlockTree blockTree,
    IEezL2Engine engine,
    IEezConfig config,
    IProcessExitSource processExit,
    ILogManager logManager) : IAsyncDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<EezFollower>();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<SettledRecord> _settled = [];

    private FollowerHeads _heads = null!;
    private BlockHeader _cursor = null!;
    private EezL1Block _lastScanned;
    private ulong _nextL1;
    private Task? _running;
    private int _disposed;

    /// <summary>Starts following; the returned task runs until the node stops.</summary>
    /// <remarks>Every failure is handled inside: transient ones are retried, the rest stop the node.</remarks>
    public Task Start() => _running ??= Run();

    private async Task Run()
    {
        CancellationToken token = _cancellation.Token;
        try
        {
            await Retrying(Boot, token);
            while (!token.IsCancellationRequested)
            {
                await Retrying(Tick, token);
                await Task.Delay(config.L1PollingIntervalMs, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (EezFollowerException e)
        {
            if (_logger.IsError) _logger.Error($"EEZ follower stopped: {e.Message}");
            processExit.Exit(ExitCodes.InvalidBlock);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("EEZ follower failed.", e);
            processExit.Exit(ExitCodes.GeneralError);
        }
    }

    /// <summary>Runs <paramref name="step"/> until L1 serves it: an incomplete read or an unreachable node is retried on the next poll.</summary>
    private async Task Retrying(Func<Task> step, CancellationToken token)
    {
        while (true)
        {
            try
            {
                await step();
                return;
            }
            catch (Exception e) when (e is L1SourceIncompleteException or HttpRequestException or DataException or TaskCanceledException
                                      && !token.IsCancellationRequested)
            {
                if (_logger.IsWarn) _logger.Warn($"EEZ follower retries after an L1 read failed: {e.Message}");
                await Task.Delay(config.L1PollingIntervalMs, token);
            }
        }
    }

    private async Task Boot()
    {
        ulong chainId = await l1.GetChainId() ?? throw new L1SourceIncompleteException(0, "L1 does not report its chain ID.");
        if (chainId != config.L1ChainId)
        {
            throw new EezFollowerException($"L1 reports chain {chainId}, not the configured chain {config.L1ChainId}.");
        }

        BlockHeader genesis = blockTree.Genesis ?? throw new EezFollowerException("The L2 chain has no genesis.");
        BlockHeader finalized = Header(blockTree.FinalizedHash) ?? genesis;
        _heads = new FollowerHeads(genesis, finalized);
        await Resume(await Latest(), fromGenesis: Header(blockTree.SafeHash) is not { } safe || safe.Number == 0);
    }

    private async Task Tick()
    {
        engine.EnsureSoleDriver();
        EezL1Block latest = await Latest();
        EezL1Block? scannedNow = await l1.GetBlockByNumber(_lastScanned.Number);
        if (scannedNow?.Hash != _lastScanned.Hash)
        {
            if (_logger.IsWarn) _logger.Warn($"L1 reorganized at or below block {_lastScanned.Number}; resuming from the last settlement that survived.");
            await Resume(latest, fromGenesis: false);
            return;
        }

        if (latest.Number >= _nextL1)
        {
            await Scan(Math.Min(latest.Number, _nextL1 + config.L1LogScanBlocks - 1));
        }

        await AdvanceFinalized();
        await AdvanceUnsafeHead();
    }

    /// <summary>Takes the sequencer's blocks as the unsafe head; its failures never hold back what L1 settled.</summary>
    private async Task AdvanceUnsafeHead()
    {
        try
        {
            await unsafeHead.Advance(_heads);
        }
        catch (Exception e) when (e is L1SourceIncompleteException or HttpRequestException or DataException or RlpException)
        {
            if (_logger.IsWarn) _logger.Warn($"EEZ follower could not read the sequencer's head: {e.Message}");
        }
    }

    /// <summary>Derives what L1 settled in blocks <see cref="_nextL1"/> to <paramref name="to"/> and moves the safe head after each block.</summary>
    private async Task Scan(ulong to)
    {
        EezL1Block end = await l1.GetBlockByNumber(to) ?? throw new L1SourceIncompleteException(to, $"L1 cannot serve block {to}.");
        ScannedBatch[] batches = await scanner.Scan(_nextL1, to);
        for (int i = 0; i < batches.Length; i++)
        {
            ScannedBatch scanned = batches[i];
            if (!scanned.Settlement.IsEmpty)
            {
                _cursor = await reconciler.Reconcile(scanned, _cursor, _heads);
            }

            bool lastInBlock = i == batches.Length - 1 || batches[i + 1].Batch.BlockHash != scanned.Batch.BlockHash;
            if (lastInBlock && _cursor.Number > _heads.Safe.Number)
            {
                _settled.Add(new SettledRecord(scanned.Batch.BlockNumber, scanned.Batch.BlockHash, _cursor));
                _heads.Safe = _cursor;
                await UpdateForkchoice();
                if (_logger.IsInfo) _logger.Info($"L2 safe head {_cursor.ToString(BlockHeader.Format.Short)}, settled in L1 block {scanned.Batch.BlockNumber}.");
            }
        }

        _lastScanned = end;
        _nextL1 = to + 1;
    }

    /// <summary>Moves the finalized head to the last settlement in a finalized L1 block.</summary>
    private async Task AdvanceFinalized()
    {
        if (await l1.GetFinalizedBlock() is not { } finalized)
        {
            return;
        }

        int last = _settled.FindLastIndex(r => r.L1Number <= finalized.Number);
        if (last < 0)
        {
            return;
        }

        SettledRecord record = _settled[last];
        _settled.RemoveRange(0, last);
        if (record.L2End.Number > _heads.Finalized.Number)
        {
            _heads.Finalized = record.L2End;
            await UpdateForkchoice();
        }
    }

    /// <summary>Continues from the last settlement L1 and the local chain agree on, or from genesis when there is none.</summary>
    private async Task Resume(EezL1Block latest, bool fromGenesis)
    {
        SettledRecord? record = fromGenesis ? null : await resumePoints.Find(latest.Number);
        _cursor = record?.L2End ?? blockTree.Genesis!;
        if (_cursor.Number < _heads.Finalized.Number)
        {
            throw new EezFollowerException($"L1 no longer settles the finalized L2 block {_heads.Finalized.ToString(BlockHeader.Format.Short)}.");
        }

        _settled.Clear();
        if (record is not null)
        {
            _settled.Add(record);
        }

        _heads.Safe = _cursor;
        _nextL1 = record is null ? config.RegistryDeployBlock : record.L1Number + 1;
        _lastScanned = await l1.GetBlockByNumber(_nextL1 - 1) ?? throw new L1SourceIncompleteException(_nextL1 - 1, $"L1 cannot serve block {_nextL1 - 1}.");
        await UpdateForkchoice();
        if (_logger.IsInfo) _logger.Info($"EEZ follower resumes from L2 block {_cursor.ToString(BlockHeader.Format.Short)} at L1 block {_nextL1}.");
    }

    private Task UpdateForkchoice()
    {
        BlockHeader head = blockTree.Head?.Header is { } current && current.Number >= _heads.Safe.Number && blockTree.IsMainChain(_heads.Safe)
            ? current
            : _heads.Safe;
        return engine.UpdateForkchoice(head.Hash!, _heads.Safe.Hash!, _heads.Finalized.Hash!);
    }

    private async Task<EezL1Block> Latest() =>
        await l1.GetLatestBlock() ?? throw new L1SourceIncompleteException(0, "L1 does not report its latest block.");

    private BlockHeader? Header(Core.Crypto.Hash256? hash) => hash is null ? null : blockTree.FindHeader(hash);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _cancellation.CancelAsync();
        if (_running is not null)
        {
            await _running;
        }

        _cancellation.Dispose();
    }
}
