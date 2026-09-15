// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Facade.Simulate;

/// <summary>
/// This is an env for eth simulator. It is constructed by <see cref="SimulateReadOnlyBlocksProcessingEnvFactory"/>.
/// It is not thread safe and is meant to be reused. <see cref="Begin"/> must be called and the returned
/// <see cref="SimulateReadOnlyBlocksProcessingScope"/> must be disposed once done or there may be some memory leak.
/// </summary>
public class SimulateReadOnlyBlocksProcessingEnv(
    IWorldState worldState,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IOverridableCodeInfoRepository codeInfoRepository,
    SimulateRequestState simulateState,
    IBlockProcessor blockProcessor,
    BlockTreeOverlay blockTreeOverlay,
    IOverridableEnv overridableEnv,
    IReadOnlyDbProvider readOnlyDbProvider
) : ISimulateReadOnlyBlocksProcessingEnv
{
    public SimulateReadOnlyBlocksProcessingScope Begin()
    {
        blockTreeOverlay.ResetMainChain();
        return new SimulateReadOnlyBlocksProcessingScope(
            worldState, specProvider, blockTree, codeInfoRepository, simulateState, blockProcessor, readOnlyDbProvider, overridableEnv
        );
    }
}

public class SimulateReadOnlyBlocksProcessingScope(
    IWorldState worldState,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IOverridableCodeInfoRepository codeInfoRepository,
    SimulateRequestState simulateState,
    IBlockProcessor blockProcessor,
    IReadOnlyDbProvider readOnlyDbProvider,
    IOverridableEnv overridableEnv
) : IDisposable
{
    private IDisposable? _overridableWorldStateCloser;

    public IWorldState WorldState => worldState;
    public ISpecProvider SpecProvider => specProvider;
    public IBlockTree BlockTree => blockTree;
    public IOverridableCodeInfoRepository CodeInfoRepository => codeInfoRepository;
    public SimulateRequestState SimulateRequestState => simulateState;
    public IBlockProcessor BlockProcessor => blockProcessor;

    /// <summary>
    /// Attempts to open the world state for the first simulated block; the following blocks chain on it inside the
    /// same scope, as the overridable env discards its overrides when the scope closes.
    /// </summary>
    /// <returns><c>false</c> when the parent state of <paramref name="firstBlock"/> is unavailable.</returns>
    public bool TryOpenAtTarget(BlockHeader firstBlock)
    {
        if (_overridableWorldStateCloser is not null) throw new InvalidOperationException("The simulate world state scope is already open.");
        return overridableEnv.TryBuildAndOverrideAtTarget(firstBlock, stateOverride: null, specOverride: null, out _overridableWorldStateCloser);
    }

    public void Dispose()
    {
        _overridableWorldStateCloser?.Dispose();
        _overridableWorldStateCloser = null;
        readOnlyDbProvider.ClearTempChanges(); // For blocktree. The read only db has a buffer that need to be cleared.
    }
}
