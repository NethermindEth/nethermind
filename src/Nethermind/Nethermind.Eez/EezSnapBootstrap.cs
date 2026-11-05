// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Follower;
using Nethermind.History;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Trie;

namespace Nethermind.Eez;

internal sealed class EezSnapBootstrap(
    IEezConfig config,
    ISyncConfig sync,
    EezChainRules rules,
    IEezL1Api l1,
    IEezSequencerApi source,
    IBlockTree blocks,
    IBlockValidator validator,
    IStateReader state,
    ISyncPointers pointers,
    IHistoryPruner history,
    ResumePointFinder resumePoints,
    [KeyFilter(DbNames.Code)] IDb code,
    ILogManager logs)
{
    private readonly TaskCompletionSource _stateSync = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Block? _pivot;

    internal Task StateSync => _stateSync.Task;

    internal void StateSyncFinished(Exception? error)
    {
        if (error is null) _stateSync.TrySetResult();
        else _stateSync.TrySetException(error);
    }

    internal async Task Prepare(CancellationToken token)
    {
        if (sync.SynchronizationEnabled && (blocks.SyncPivot.BlockNumber != sync.PivotNumber || blocks.SyncPivot.BlockHash != new Hash256(sync.PivotHash!)))
            throw Refused("The persisted sync pivot differs from the requested pivot.");
        if (sync.SynchronizationEnabled && blocks.Head?.Number > 0)
            throw Refused("A SNAP download cannot replace an existing processed chain.");
        EezL1Block anchor = await Authorize(token);
        Hash256 hash = new(sync.PivotHash!);
        if (!sync.SynchronizationEnabled && blocks.Head is { } descendant
            && (descendant.Number > sync.PivotNumber
                || descendant.Number == sync.PivotNumber && (blocks.SafeHash is not null || blocks.FinalizedHash is not null)))
        {
            BlockHeader held = blocks.FindHeader(hash) ?? throw Refused("The bootstrap pivot header is missing.");
            ValidatePivotHeader(held, hash);
            _pivot = new Block(held);
            VerifyDescendant(descendant, token);
            return;
        }
        Block? pivot = blocks.FindBlock(hash);
        if (pivot is null)
        {
            byte[] raw = await source.GetRawBlock(hash, token)
                ?? throw Refused("The source does not serve the configured pivot block.");
            pivot = Rlp.Decode<Block>(raw) ?? throw Refused("The pivot block cannot be decoded.");
        }
        ValidatePivotHeader(pivot.Header, hash);
        if (!validator.ValidateBodyAgainstHeader(pivot.Header, pivot.Body, out string? error))
            throw Refused($"The pivot body is invalid: {error}");
        pivot.Header.TotalDifficulty = UInt256.Zero;
        _pivot = pivot;

        if (sync.SynchronizationEnabled)
        {
            if (blocks.Insert(pivot, BlockTreeInsertBlockOptions.SaveHeader, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded) != AddBlockResult.Added)
                throw Refused("The block tree cannot store the authorized pivot.");
        }
        else
        {
            VerifySnapshot(token);
            SettledRecord? recovery = await resumePoints.Find(anchor.Number, token);
            if (recovery?.L2End.Hash != pivot.Hash)
                throw Refused("The downloaded ancestry cannot recover the authorized settlement and descendant seed.");
            await Authorize(token);
            if (!blocks.TryUpdateMainChain(pivot.Header, wereProcessed: true, forceUpdateHeadBlock: true, pivot))
                throw Refused("The downloaded pivot cannot become a processed checkpoint.");
            blocks.ForkChoiceUpdated(hash, hash);
        }
    }

    internal async Task VerifyDownload(CancellationToken token)
    {
        await StateSync.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        while (blocks.LowestInsertedHeader?.Number is not <= 1
            || sync.DownloadBodiesInFastSync && (pointers.LowestInsertedBodyNumber ?? ulong.MaxValue) > Math.Max(sync.AncientBodiesBarrierCalc, history.CutoffBlockNumber ?? 0)
            || sync.DownloadReceiptsInFastSync && (pointers.LowestInsertedReceiptBlockNumber ?? ulong.MaxValue) > Math.Max(sync.AncientReceiptsBarrierCalc, history.CutoffBlockNumber ?? 0))
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        VerifySnapshot(token);
        await Authorize(token);
    }

    private void ValidatePivotHeader(BlockHeader header, Hash256 hash)
    {
        if (header.Number != sync.PivotNumber || header.CalculateHash() != hash || header.Hash != hash
            || rules.Schedule!.Timestamp(header.Number) != header.Timestamp)
            throw Refused("The pivot header does not match its configured height, commitment or native schedule.");
    }

    private void VerifyDescendant(Block head, CancellationToken token)
    {
        Block pivot = _pivot!;
        if (!IsProcessedCanonical(head.Header) || !state.HasStateForBlock(head.Header))
            throw Refused("The existing descendant does not have usable processed state.");
        if (blocks.SafeHash is not { } safeHash || blocks.FindHeader(safeHash) is not { } safe
            || blocks.FinalizedHash is not { } finalizedHash || blocks.FindHeader(finalizedHash) is not { } finalized
            || finalized.Number < pivot.Number || finalized.Number > safe.Number
            || !IsProcessedCanonical(safe) || !IsProcessedCanonical(finalized))
            throw Refused("The existing descendant does not have compatible processed safe/finalized heads.");
        // Startup loads Head from durable state; stored forkchoice can be ahead until native recovery replays it.
        BlockHeader current = safe.Number > head.Number ? safe : head.Header;
        while (current.Number > pivot.Number)
        {
            token.ThrowIfCancellationRequested();
            if (current.CalculateHash() != current.Hash || !IsProcessedCanonical(current))
                throw Refused("The processed descendant has invalid ancestry.");
            BlockHeader parent = blocks.FindHeader(current.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded)
                ?? throw Refused("The processed descendant ancestry is incomplete.");
            if (parent.Number != current.Number - 1)
                throw Refused("The processed descendant ancestry has a height gap.");
            current = parent;
        }
        if (current.Hash != pivot.Hash || current.CalculateHash() != pivot.Hash || !blocks.IsMainChain(current))
            throw Refused("The processed head does not descend from the authorized pivot.");
    }

    private bool IsProcessedCanonical(BlockHeader header)
    {
        (BlockInfo? info, ChainLevelInfo? level) = blocks.GetInfo(header.Number, header.Hash!);
        return info is { WasProcessed: true } && level?.MainChainBlock?.BlockHash == header.Hash;
    }

    private void VerifySnapshot(CancellationToken token)
    {
        Block pivot = _pivot ?? throw Refused("The pivot has not been authorized.");
        BlockHeader current = blocks.FindHeader(pivot.Hash!)
            ?? throw Refused("The downloaded pivot header is missing.");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (current.CalculateHash() != current.Hash || !blocks.IsMainChain(current))
                throw Refused("The downloaded header ancestry is invalid or noncanonical.");
            if (current.Number == 0)
            {
                if (current.Hash != blocks.Genesis?.Hash)
                    throw Refused("The downloaded ancestry does not reach the local genesis.");
                break;
            }
            BlockHeader parent = blocks.FindHeader(current.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded)
                ?? throw Refused("The downloaded header ancestry is incomplete.");
            if (parent.Number != current.Number - 1)
                throw Refused("The downloaded header ancestry has a height gap.");
            current = parent;
        }
        if (blocks.FindBlock(pivot.Hash!) is not { } stored
            || !validator.ValidateBodyAgainstHeader(pivot.Header, stored.Body, out _))
            throw Refused("The downloaded pivot body is unavailable or invalid.");
        if (!state.HasStateForBlock(pivot.Header))
            throw Refused("The downloaded pivot state is unavailable.");
        TrieStatsCollector collector = new(code, logs, token);
        state.RunTreeVisitor(collector, pivot.Header, new VisitingOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            FullScanMemoryBudget = 64 * 1024 * 1024,
        });
        collector.Finish();
        token.ThrowIfCancellationRequested();
        if (collector.Stats.MissingNodes != 0)
            throw Refused("The downloaded state has missing trie nodes or bytecode.");
    }

    private async Task<EezL1Block> Authorize(CancellationToken token)
    {
        if (await l1.GetChainId(token) != config.L1ChainId)
            throw Refused("The L1 chain ID differs from the configured chain.");
        RollupXL1Reference reference = rules.Schedule!.L1Reference;
        EezL1Block origin = await Block(reference.Number, token);
        if (origin.Hash != reference.Hash || origin.Timestamp != reference.Timestamp || origin.MixHash != reference.PrevRandao)
            throw Refused("L1 does not match the genesis-pinned reference.");
        EezL1Block finalized = await l1.GetFinalizedBlock(token)
            ?? throw Refused("L1 does not report a finalized block.");
        Address registry = new(config.RegistryAddress!);
        Hash256 rollup = L1BatchScanner.RollupTopic(config.RollupId);
        Hash256 pivot = new(sync.PivotHash!);
        ulong to = finalized.Number;
        while (to >= config.RegistryDeployBlock)
        {
            ulong from = to - config.RegistryDeployBlock + 1 > config.L1LogScanBlocks
                ? to - config.L1LogScanBlocks + 1 : config.RegistryDeployBlock;
            List<EezL1Log> logs = await l1.GetAllLogs(registry, L1BatchScanner.L2ExecutionPerformedTopic, rollup, from, to, token);
            Dictionary<ulong, EezL1Log> last = [];
            foreach (EezL1Log log in logs)
            {
                if (log.Address != registry || log.Topics is not { Length: 2 } || log.Topics[0] != L1BatchScanner.L2ExecutionPerformedTopic
                    || log.Topics[1] != rollup || log.BlockNumber < from || log.BlockNumber > to)
                    throw Refused("L1 returned a settlement outside the requested rollup or range.");
                if (!last.TryGetValue(log.BlockNumber, out EezL1Log previous)
                    || log.TransactionIndex > previous.TransactionIndex
                    || log.TransactionIndex == previous.TransactionIndex && log.LogIndex > previous.LogIndex)
                    last[log.BlockNumber] = log;
            }
            foreach (EezL1Log log in last.Values)
            {
                if (L1BatchScanner.SettledRootOf(log) is not { } root || new Hash256(root) != pivot) continue;
                EezL1Block settling = await Block(log.BlockNumber, token);
                if (rules.Schedule.AnchorKind(sync.PivotNumber, settling.Timestamp) is null)
                    throw Refused("The pivot cannot be settled at the reported L1 slot.");
                if (settling.Hash != log.BlockHash || (await Block(finalized.Number, token)).Hash != finalized.Hash)
                    throw Refused("L1 changed during pivot authorization.");
                return settling;
            }
            if (from == config.RegistryDeployBlock) break;
            to = from - 1;
        }
        throw Refused("The pivot is not a final rollup commitment in canonical finalized L1 history.");
    }

    private async Task<EezL1Block> Block(ulong number, CancellationToken token)
    {
        EezL1Block block = await l1.GetBlockByNumber(number, token) ?? throw Refused($"L1 block {number} is unavailable.");
        if (block.Number != number || block.Hash is null)
            throw Refused($"L1 returned another block for height {number}.");
        return block;
    }

    private static EezFollowerException Refused(string message) => new($"EEZ SNAP bootstrap refused: {message}");
}
