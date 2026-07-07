// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

/// <summary>Reuses a single <see cref="IBalProcessingEnv"/> worker for the whole block.</summary>
public class SequentialBalEnvManager : ISequentialBalEnvManager
{
    private readonly IBalProcessingEnv _balEnv;

    public SequentialBalEnvManager(IBalProcessingEnvFactory envFactory)
    {
        _balEnv = envFactory.Create(parallel: false);
        _balEnv.WorldState.SetGeneratingBlockAccessList(new());
    }

    public void Setup(Block block, BlockExecutionContext blockExecutionContext, Hash256? parentStateRoot)
        => _balEnv.Setup(block, blockExecutionContext, 0u, parentReader: null);

    public IBalProcessingEnv Get(uint? _)
        => _balEnv;

    public void NextTransaction()
    {
        _balEnv.WorldState.Clear();
        _balEnv.WorldState.IncrementIndex();
    }

    public void Rollback() => _balEnv.WorldState.Clear();

    public void Dispose() => _balEnv.Dispose();

    public void MergeAndReturnBal(uint _, GeneratedBlockAccessList? target, Action<BlockAccessListAtIndex>? onSlice = null)
    {
        BlockAccessListAtIndex slice = _balEnv.WorldState.GetGeneratingBlockAccessList()!;
        target?.Merge(slice);
        onSlice?.Invoke(slice);
    }
}
