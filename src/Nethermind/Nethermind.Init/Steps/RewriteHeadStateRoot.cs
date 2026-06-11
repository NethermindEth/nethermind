// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

/// <summary>
/// Rewrites the current head block so its <see cref="BlockHeader.StateRoot"/> equals a configured hash, recomputes
/// the block hash, and re-points the canonical chain plus head/safe/finalized pointers to the new block hash.
///
/// This is the recovery follow-up to a flat-trie rebuild that produced a NEW (consistent) state root: the persisted
/// head header still advertises the old, now-unservable state root, which both blocks boot ("no state available for
/// head") and makes the snap pivot derive the wrong target root. After this step a fresh boot loads the same head
/// number with the new block hash and the new state root.
///
/// Strictly additive: only runs when <see cref="IFlatDbConfig.RewriteHeadStateRoot"/> is set. Idempotent: re-running
/// with the same value simply re-asserts the already-canonical new block.
/// </summary>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class RewriteHeadStateRoot(
    IBlockTree blockTree,
    IProcessExitSource exitSource,
    IFlatDbConfig flatDbConfig,
    ILogManager logManager
) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<RewriteHeadStateRoot>();

    public Task Execute(CancellationToken cancellationToken)
    {
        string? configured = flatDbConfig.RewriteHeadStateRoot;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Task.CompletedTask;
        }

        Hash256 newStateRoot;
        try
        {
            newStateRoot = new Hash256(Bytes.FromHexString(configured));
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"RewriteHeadStateRoot: invalid state root hash '{configured}'.", ex);
            exitSource.Exit(1);
            return Task.CompletedTask;
        }

        Block? headBlock = blockTree.Head;
        if (headBlock is null || headBlock.Header.Hash is null)
        {
            if (_logger.IsError) _logger.Error("RewriteHeadStateRoot: no head block available; cannot rewrite.");
            exitSource.Exit(1);
            return Task.CompletedTask;
        }

        Hash256 oldHash = headBlock.Header.Hash;
        Hash256? oldStateRoot = headBlock.Header.StateRoot;
        ulong number = headBlock.Number;

        if (oldStateRoot == newStateRoot)
        {
            // Already rewritten (idempotent re-run): head header already carries the requested state root.
            if (_logger.IsWarn) _logger.Warn(
                $"RewriteHeadStateRoot: head block {number} already has state root {newStateRoot}. Nothing to do. Head hash: {oldHash}.");
            exitSource.Exit(0);
            return Task.CompletedTask;
        }

        // Clone preserves every header field (parentHash, txRoot, receiptsRoot, TotalDifficulty, Bloom, ...).
        BlockHeader newHeader = headBlock.Header.Clone();
        newHeader.StateRoot = newStateRoot;
        newHeader.Hash = null;
        newHeader.Hash = newHeader.CalculateHash();

        Hash256 newHash = newHeader.Hash;
        Block newBlock = headBlock.WithReplacedHeader(newHeader);

        if (_logger.IsWarn) _logger.Warn(
            $"RewriteHeadStateRoot: rewriting head block {number}. Old hash {oldHash} (stateRoot {oldStateRoot}) -> new hash {newHash} (stateRoot {newStateRoot}).");

        try
        {
            // 1) Persist the new block: body + block number + header, creating its main-chain level/BlockInfo (TD is
            //    copied from the original head header so head-improvement checks remain consistent).
            blockTree.Insert(
                newBlock,
                BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks,
                BlockTreeInsertHeaderOptions.None);

            // 2) Make the new block canonical at its level and force it to be the head (sets HEAD pointer +
            //    BestPersistedState reorg boundary). The old hash remains as a non-main side block at the level.
            blockTree.TryUpdateMainChain(newHeader, wereProcessed: true, forceUpdateHeadBlock: true, newBlock);

            // 3) Re-point finalized + safe to the new hash (persists FinalizedBlockHash / SafeBlockHash metadata).
            blockTree.ForkChoiceUpdated(newHash, newHash);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("RewriteHeadStateRoot: failed to re-point the chain to the rewritten head block.", ex);
            exitSource.Exit(1);
            return Task.CompletedTask;
        }

        if (_logger.IsWarn) _logger.Warn(
            $"RewriteHeadStateRoot: DONE. Head is now block {number} hash {newHash} stateRoot {newStateRoot}. " +
            $"Use {newHash} as the snap PivotHash. Previous head hash was {oldHash}.");

        exitSource.Exit(0);
        return Task.CompletedTask;
    }
}
