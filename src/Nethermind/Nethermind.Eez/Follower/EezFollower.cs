// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Follower;

/// <summary>
/// Follows the rollup from L1: scans L1 for the batches that settled it, derives every block they settle and moves
/// the safe head to what L1 stores, and the finalized head to what a finalized L1 block stores. An L1 reorganization
/// sends it back to the last settlement that survived. A settlement the local chain cannot reproduce stops the node,
/// since serving blocks L1 disagrees with would be worse than serving none. Progress is kept per L1 block, so a read
/// that fails resumes at the block it failed in. <see cref="EezDriver"/> runs it.
/// </summary>
public sealed class EezFollower(
    IEezL1Api l1,
    IL1BatchScanner scanner,
    IBatchReconciler reconciler,
    ResumePointFinder resumePoints,
    IUnsafeHeadSource unsafeHead,
    IBlockTree blockTree,
    IEezL2Engine engine,
    IEezConfig config,
    ILogManager logManager)
{
    /// <summary>
    /// How many settling L1 blocks are kept while L1 finalizes none of them, as on a chain without a finalized tag; an
    /// older one could only finalize an L2 block a newer one finalizes too.
    /// </summary>
    internal const int MaxPendingSettlements = 4096;

    /// <summary>How long one poll may wait on the sequencer, so a slow one never holds back what L1 settled.</summary>
    internal static readonly TimeSpan SequencerBudget = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = logManager.GetClassLogger<EezFollower>();
    private readonly Queue<SettledRecord> _settled = new();

    private FollowerHeads _heads = null!;
    private BlockHeader _cursor = null!;
    private EezL1Block _lastScanned;
    private Hash256? _followedLatest;
    private ulong _nextL1;

    internal FollowerHeads Heads => _heads;

    /// <summary>The latest L1 block the last poll saw, or <see langword="null"/> before the first.</summary>
    public EezL1Block? LatestL1 { get; private set; }

    /// <returns>Whether the follower booted; a transient failure is logged and left to the next attempt.</returns>
    public Task<bool> TryBoot(CancellationToken token) => Attempt(Boot, token);

    /// <summary>Finds where to resume from, on L1 and on the local chain.</summary>
    internal async Task Boot(CancellationToken token)
    {
        ulong chainId = await l1.GetChainId(token) ?? throw new L1SourceIncompleteException(0, "L1 does not report its chain ID.");
        if (chainId != config.L1ChainId)
        {
            throw new EezFollowerException($"L1 reports chain {chainId}, not the configured chain {config.L1ChainId}.");
        }

        BlockHeader genesis = blockTree.Genesis ?? throw new EezFollowerException("The L2 chain has no genesis.");
        _heads = new FollowerHeads(engine, blockTree, Header(blockTree.FinalizedHash) ?? genesis);
        await Resume(await Latest(token), fromGenesis: Header(blockTree.SafeHash) is not { } safe || safe.Number == 0, token);
    }

    /// <summary>One poll: what L1 settled since the last one, then the sequencer's head.</summary>
    /// <returns>Whether L1 has more blocks to scan right away.</returns>
    internal async Task<bool> Poll(CancellationToken token)
    {
        engine.EnsureSoleDriver();
        bool behind = await Attempt(FollowL1, token) && _followedLatest is null;
        await AdvanceUnsafeHead(token);
        return behind;
    }

    /// <returns>Whether <paramref name="step"/> completed; a transient failure is logged and left to the next poll.</returns>
    private async Task<bool> Attempt(Func<CancellationToken, Task> step, CancellationToken token)
    {
        try
        {
            await step(token);
            return true;
        }
        catch (Exception e) when (IsTransient(e, token))
        {
            if (_logger.IsWarn) _logger.Warn($"EEZ follower retries on the next poll: {e.Message}");
            return false;
        }
    }

    /// <summary>Whether <paramref name="e"/> clears on its own: an L1 or engine read that failed or came back incomplete.</summary>
    internal static bool IsTransient(Exception e, CancellationToken token) =>
        !token.IsCancellationRequested
        && e is L1SourceIncompleteException or EezEngineUnavailableException or HttpRequestException or DataException or OperationCanceledException;

    private async Task FollowL1(CancellationToken token)
    {
        EezL1Block latest = await Latest(token);
        LatestL1 = latest;
        if (latest.Hash == _followedLatest)
        {
            return;
        }

        _followedLatest = null;
        if (await l1.GetBlockByNumber(_lastScanned.Number, token) is not { } scanned || scanned.Hash != _lastScanned.Hash)
        {
            if (_logger.IsWarn) _logger.Warn($"L1 reorganized at or below block {_lastScanned.Number}; resuming from the last settlement that survived.");
            await Resume(latest, fromGenesis: false, token);
        }

        if (latest.Number >= _nextL1)
        {
            await Scan(Math.Min(latest.Number, _nextL1 + config.L1LogScanBlocks - 1), token);
        }

        await AdvanceFinalized(token);
        if (_nextL1 > latest.Number)
        {
            _followedLatest = latest.Hash;
        }
    }

    /// <summary>Derives what L1 settled in blocks <see cref="_nextL1"/> to <paramref name="to"/>, one settling block at a time.</summary>
    private async Task Scan(ulong to, CancellationToken token)
    {
        EezL1Block end = await Block(to, token);
        foreach (L1SettlingBlock block in await scanner.FindSettlingBlocks(_nextL1, to, token))
        {
            await Settle(block, token);
            _lastScanned = new EezL1Block { Number = block.Number, Hash = block.Hash };
            _nextL1 = block.Number + 1;
        }

        _lastScanned = end;
        _nextL1 = to + 1;
    }

    /// <summary>
    /// Reconciles every batch of <paramref name="block"/> and moves safe to where L1's commitment stands after it. The
    /// cursor moves only once the whole block is settled, so a retry starts the block over.
    /// </summary>
    private async Task Settle(L1SettlingBlock block, CancellationToken token)
    {
        ScannedBatch[] batches = await scanner.Scan(block, token);
        BlockHeader cursor = _cursor;
        try
        {
            foreach (ScannedBatch scanned in batches)
            {
                if (!scanned.Settlement.IsEmpty)
                {
                    cursor = await reconciler.Reconcile(scanned, cursor, _heads);
                }
            }
        }
        catch (EezFollowerException e)
        {
            if (await IsCanonical(block.Number, block.Hash, token))
            {
                throw;
            }

            throw new L1SourceIncompleteException(block.Number, $"L1 reorganized block {block.Number} while it was derived: {e.Message}");
        }

        if (cursor.Number > _heads.Safe.Number)
        {
            await _heads.AdvanceSafe(cursor);
            Remember(new SettledRecord(block.Number, block.Hash, cursor));
            if (_logger.IsInfo) _logger.Info($"L2 safe head {cursor.ToString(BlockHeader.Format.Short)}, settled in L1 block {block.Number}.");
        }

        _cursor = cursor;
    }

    private void Remember(SettledRecord record)
    {
        if (_settled.Count == MaxPendingSettlements)
        {
            _settled.Dequeue();
        }

        _settled.Enqueue(record);
    }

    /// <summary>Moves the finalized head to the last settlement in a finalized L1 block.</summary>
    private async Task AdvanceFinalized(CancellationToken token)
    {
        if (await l1.GetFinalizedBlock(token) is not { } finalized)
        {
            return;
        }

        SettledRecord? last = null;
        while (_settled.TryPeek(out SettledRecord? record) && record.L1Number <= finalized.Number)
        {
            last = _settled.Dequeue();
        }

        if (last is not null && last.L2End.Number > _heads.Finalized.Number && last.L2End.Number <= _heads.Safe.Number)
        {
            await _heads.AdvanceFinalized(last.L2End);
        }
    }

    /// <summary>Continues from the last settlement L1 and the local chain agree on, or from genesis when there is none.</summary>
    private async Task Resume(EezL1Block latest, bool fromGenesis, CancellationToken token)
    {
        SettledRecord? record = fromGenesis ? null : await resumePoints.Find(latest.Number, token);
        BlockHeader cursor = record?.L2End ?? blockTree.Genesis!;
        ulong next = record is null ? config.RegistryDeployBlock : record.L1Number + 1;
        EezL1Block lastScanned = await Block(next - 1, token);
        await _heads.Reset(cursor);

        _settled.Clear();
        if (record is not null)
        {
            _settled.Enqueue(record);
        }

        _cursor = cursor;
        _nextL1 = next;
        _lastScanned = lastScanned;
        _followedLatest = null;
        if (_logger.IsInfo) _logger.Info($"EEZ follower resumes from L2 block {cursor.ToString(BlockHeader.Format.Short)} at L1 block {next}.");
    }

    /// <summary>Takes the sequencer's blocks as the unsafe head; its failures never hold back what L1 settled.</summary>
    private async Task AdvanceUnsafeHead(CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(SequencerBudget);
        try
        {
            await unsafeHead.Advance(_heads, deadline.Token);
        }
        catch (Exception e) when (IsTransient(e, token) || (e is RlpException && !token.IsCancellationRequested))
        {
            if (_logger.IsWarn) _logger.Warn($"EEZ follower could not take the sequencer's head: {e.Message}");
        }
    }

    private async Task<bool> IsCanonical(ulong number, Hash256 hash, CancellationToken token) =>
        (await l1.GetBlockByNumber(number, token))?.Hash == hash;

    private async Task<EezL1Block> Block(ulong number, CancellationToken token) =>
        await l1.GetBlockByNumber(number, token) ?? throw new L1SourceIncompleteException(number, $"L1 cannot serve block {number}.");

    private async Task<EezL1Block> Latest(CancellationToken token) =>
        await l1.GetLatestBlock(token) ?? throw new L1SourceIncompleteException(0, "L1 does not report its latest block.");

    private BlockHeader? Header(Hash256? hash) => hash is null ? null : blockTree.FindHeader(hash);
}
