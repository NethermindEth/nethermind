// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Facade.Simulate;

/// <summary>
/// This is an env for eth simulater. It is constructed by <see cref="SimulateReadOnlyBlocksProcessingEnvFactory"/>.
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
    public SimulateReadOnlyBlocksProcessingScope Begin(BlockHeader? baseBlock)
    {
        blockTreeOverlay.ResetMainChain();
        IDisposable envDisposer = overridableEnv.BuildAndOverride(baseBlock, null);
        return new SimulateReadOnlyBlocksProcessingScope(
            worldState, specProvider, blockTree, codeInfoRepository, simulateState, blockProcessor, readOnlyDbProvider, envDisposer
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
    IDisposable overridableWorldStateCloser
) : IDisposable
{
    public IWorldState WorldState => worldState;
    public ISpecProvider SpecProvider => specProvider;
    public IBlockTree BlockTree => blockTree;
    public IOverridableCodeInfoRepository CodeInfoRepository => codeInfoRepository;
    public SimulateRequestState SimulateRequestState => simulateState;
    public IBlockProcessor BlockProcessor => blockProcessor;

    public void Dispose()
    {
        overridableWorldStateCloser.Dispose();
        readOnlyDbProvider.Dispose(); // For blocktree. The read only db has a buffer that need to be cleared.
    }
}
