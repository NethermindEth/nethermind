// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Re-attaches the stored EIP-7928 block access list to a block queued for processing without one.
/// </summary>
/// <remarks>
/// The access list travels beside a block (engine API) rather than in its RLP, so a block reloaded from the
/// block store has none: the replay from the persisted state after a restart, or a reorg back onto a stored
/// branch. Without it such a block falls back to sequential execution without access-list read warming.
/// A stored list is attached only when its wire hash matches the header's commitment and it is within the EIP-7928
/// item limit, which a list stored by sync was never checked against; otherwise the block keeps the sequential path,
/// where the limit is checked on the list execution builds.
/// A stored list is looked up only when parallel execution, read warming, or finalized catch-up needs it.
/// </remarks>
public sealed class BlockAccessListRecoveryStep(IBlockAccessListStore balStore, IBlocksConfig blocksConfig, ILogManager logManager, ISyncConfig? syncConfig = null) : IBlockPreprocessorStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockAccessListRecoveryStep>();
    private readonly bool _hasConsumer = ExecutionUsesAccessLists(blocksConfig) || syncConfig?.ReconstructFinalizedStateFromBlockAccessLists == true;

    /// <summary>Whether block execution reads a block's access list, for parallel execution or read warming.</summary>
    /// <remarks>The zkEVM build does not execute in parallel, so there only read warming reads the list.</remarks>
    public static bool ExecutionUsesAccessLists(IBlocksConfig blocksConfig) =>
        (!ZkEvmFlag.IsActive && blocksConfig.ParallelExecution) || blocksConfig.ParallelExecutionBatchRead;

    /// <inheritdoc/>
    /// <remarks>Only queued processing re-attaches the stored list; tracing and one-time processing run sequentially.</remarks>
    public void RecoverData(Block block) { }

    /// <inheritdoc/>
    public void RecoverDataForQueuedProcessing(Block block)
    {
        if (!_hasConsumer || block.BlockAccessList is not null || block.Header.BlockAccessListHash is not { } expectedHash || block.Hash is not { } blockHash)
            return;

        ReadOnlyBlockAccessList? stored;
        try
        {
            stored = balStore.Get(block.Number, blockHash);
        }
        catch (RlpException ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Ignoring undecodable stored block access list of {block.ToString(Block.Format.Short)}: {ex.Message}");
            return;
        }

        if (stored is null || stored.WireHash != expectedHash) return;

        // A stored list skipped the engine API's pre-execution item limit check. The validator would still reject
        // the block afterwards, but an over-limit list would first drive prewarming, the index build and parallel
        // execution, so leave it off and let the sequential path check the generated list after execution.
        if (Eip7928Constants.ExceedsItemLimit(stored.ItemCount, block.Header.GasLimit))
        {
            if (_logger.IsDebug) _logger.Debug($"Ignoring stored block access list of {block.ToString(Block.Format.Short)}: {stored.ItemCount} items exceed the limit of {block.Header.GasLimit / Eip7928Constants.ItemCost}");
            return;
        }

        block.BlockAccessList = stored;
    }
}
