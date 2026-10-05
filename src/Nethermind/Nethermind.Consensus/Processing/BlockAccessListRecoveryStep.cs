// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BlockAccessLists;
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
/// A stored list is attached only when its wire hash matches the header's commitment, so it is exactly the list
/// the block was validated with; otherwise the block keeps the sequential path.
/// Nothing is looked up when neither parallel execution nor access-list read warming is enabled, since no
/// consumer would use the list.
/// </remarks>
public sealed class BlockAccessListRecoveryStep(IBlockAccessListStore balStore, IBlocksConfig blocksConfig, ILogManager logManager) : IBlockPreprocessorStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<BlockAccessListRecoveryStep>();
    private readonly bool _hasConsumer = (ExecutionFlags.ParallelExecution && blocksConfig.ParallelExecution) || blocksConfig.ParallelExecutionBatchRead;

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

        if (stored?.WireHash == expectedHash)
        {
            block.BlockAccessList = stored;
        }
    }
}
