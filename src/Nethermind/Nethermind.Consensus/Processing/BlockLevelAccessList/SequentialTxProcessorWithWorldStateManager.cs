// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>Reuses a single <see cref="IBalProcessingEnv"/> worker for the whole block.</summary>
public class SequentialTxProcessorWithWorldStateManager : ISequentialTxProcessorWithWorldStateManager
{
    private readonly IBalProcessingEnv _txProcessorWithWorldState;

    public SequentialTxProcessorWithWorldStateManager(IBalProcessingEnvFactory envFactory)
    {
        _txProcessorWithWorldState = envFactory.Create(parallel: false);
        _txProcessorWithWorldState.WorldState.SetGeneratingBlockAccessList(new());
    }

    public void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot)
        => _txProcessorWithWorldState.Setup(block, blockExecutionContext, 0u, parentReader: null);

    public IBalProcessingEnv Get(uint? _)
        => _txProcessorWithWorldState;

    public void NextTransaction()
    {
        _txProcessorWithWorldState.WorldState.Clear();
        _txProcessorWithWorldState.WorldState.IncrementIndex();
    }

    public void Rollback() => _txProcessorWithWorldState.WorldState.Clear();

    public void Dispose() => _txProcessorWithWorldState.Dispose();

    public void MergeAndReturnBal(uint _, GeneratedBlockAccessList? target, Action<BlockAccessListAtIndex>? onSlice = null)
    {
        BlockAccessListAtIndex slice = _txProcessorWithWorldState.WorldState.GetGeneratingBlockAccessList()!;
        target?.Merge(slice);
        onSlice?.Invoke(slice);
    }
}
