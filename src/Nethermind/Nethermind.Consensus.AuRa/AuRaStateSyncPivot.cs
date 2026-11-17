// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Uses the trusted sync anchor until block processing can read the validator contract from the downloaded state.
/// </summary>
public sealed class AuRaStateSyncPivot(
    IStateSyncPivot inner,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    ILogManager logManager,
    IProcessExitSource? processExitSource = null) : IStateSyncPivot
{
    private readonly ILogger _logger = logManager.GetClassLogger<AuRaStateSyncPivot>();
    private BlockHeader? _pivot;
    private int _stoppedOnPreMergeSyncPivot;

    /// <summary>Whether the active validator set can change without a chainspec transition.</summary>
    public static bool IsNeededFor(AuRaChainSpecEngineParameters.AuRaValidatorJson? validators) =>
        validators is not null && validators.List is null;

    /// <inheritdoc/>
    public ulong Diff => (blockTree.BestSuggestedHeader?.Number ?? 0UL).SaturatingSub(Volatile.Read(ref _pivot)?.Number ?? 0UL);

    /// <inheritdoc/>
    public ConcurrentHashSet<Hash256> UpdatedStorages => inner.UpdatedStorages;

    /// <inheritdoc/>
    public bool CanFinalize(BlockHeader pivot) =>
        GetPivotHeader()?.Hash == pivot.Hash && blockTree.IsMainChain(pivot) && inner.CanFinalize(pivot);

    /// <inheritdoc/>
    public void UpdateHeaderForcefully() => inner.UpdateHeaderForcefully();

    /// <inheritdoc/>
    public BlockHeader? GetPivotHeader()
    {
        BlockHeader? candidate = inner.GetPivotHeader();
        BlockHeader? pivot = candidate is null ? null : SelectPivot(candidate);
        Volatile.Write(ref _pivot, pivot);
        return pivot;
    }

    private BlockHeader? SelectPivot(BlockHeader candidate)
    {
        (ulong number, Hash256? hash) = blockTree.SyncPivot;
        hash ??= number == 0 ? blockTree.Genesis?.Hash : null;
        if (hash is null || candidate.Number < number) return null;

        BlockHeader? anchor = blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded, number);
        if (anchor is null) return null;

        if (specProvider.TerminalTotalDifficulty is { } terminalTotalDifficulty)
        {
            if (anchor.Difficulty.IsZero && (!anchor.IsGenesis || terminalTotalDifficulty.IsZero)) return candidate;
            if (blockTree.FinalizedHash is { } finalizedHash && finalizedHash != Keccak.Zero &&
                blockTree.FindHeader(finalizedHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded) is { IsGenesis: false } finalized &&
                finalized.Difficulty.IsZero)
            {
                StopOnPreMergeSyncPivot(number);
                return null;
            }
        }

        return blockTree.IsMainChain(anchor) ? anchor : null;
    }

    private void StopOnPreMergeSyncPivot(ulong number)
    {
        if (Interlocked.Exchange(ref _stoppedOnPreMergeSyncPivot, 1) != 0) return;

        if (_logger.IsError)
            _logger.Error($"State sync cannot start: the configured sync pivot {number} is before the merge. Set Sync.PivotNumber and Sync.PivotHash to a block after the merge.");
        processExitSource?.Exit(ExitCodes.ConflictingConfigurations);
    }
}
