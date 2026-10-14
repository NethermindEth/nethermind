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
/// A stored list is attached only when its wire hash matches the header's commitment and its item count is within
/// the block's limit; otherwise the block keeps the sequential path.
/// A stored list is looked up only when parallel execution, read warming, or finalized catch-up needs it.
/// </remarks>
public sealed class BlockAccessListRecoveryStep(IBlockAccessListStore balStore, IBlocksConfig blocksConfig, ILogManager logManager, ISyncConfig? syncConfig = null) : IBlockPreprocessorStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockAccessListRecoveryStep>();
    private readonly bool _hasConsumer = (!ZkEvmFlag.IsActive && blocksConfig.ParallelExecution) || blocksConfig.ParallelExecutionBatchRead
        || syncConfig?.ReconstructFinalizedStateFromBlockAccessLists == true;

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

        // The engine API path checks the item limit before processing; a list that reaches the block from the
        // store skipped that check, so one over the limit is left off and the block takes the sequential path,
        // where the generated list is checked against the limit after execution.
        if (ExceedsItemLimit(stored, block.Header))
        {
            if (_logger.IsDebug) _logger.Debug($"Ignoring stored block access list of {block.ToString(Block.Format.Short)}: {stored.ItemCount} items exceed the limit of {block.Header.GasLimit / Eip7928Constants.ItemCost}");
            return;
        }

        block.BlockAccessList = stored;
    }

    private static bool ExceedsItemLimit(ReadOnlyBlockAccessList bal, BlockHeader header) =>
        (ulong)bal.ItemCount > header.GasLimit / Eip7928Constants.ItemCost;
}
