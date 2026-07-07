// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>
/// Hands out the per-block <see cref="IBalProcessingEnv"/> worker(s) and drives their per-tx
/// lifecycle. Two implementations exist:
///   * <see cref="IParallelTxProcessorWithWorldStateManager"/> rents/returns a processor per tx
///     index from a bounded pool, staging each tx's BAL slice so the validator can merge them in
///     canonical order.
///   * <see cref="ISequentialTxProcessorWithWorldStateManager"/> reuses a single processor for the
///     whole block.
/// </summary>
internal interface ITxProcessorWithWorldStateManager : IDisposable
{
    void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot);
    IBalProcessingEnv Get(uint? balIndex = null);
    IBalProcessingEnv GetPreExecution() => Get(0u);
    IBalProcessingEnv GetPostExecution() => Get(uint.MaxValue);
    void NextTransaction();
    void Rollback();
    void MergeAndReturnBal(uint balIndex, GeneratedBlockAccessList? target, Action<BlockAccessListAtIndex>? onSlice = null);
}
