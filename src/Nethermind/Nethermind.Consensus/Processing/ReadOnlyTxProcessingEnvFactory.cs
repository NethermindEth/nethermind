// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingEnvFactory(
    IWorldStateManager worldStateManager,
    IReadOnlyBlockTree readOnlyBlockTree,
    ISpecProvider specProvider,
    ILogManager logManager,
    IPrecompileChecker precompileChecker) : IReadOnlyTxProcessingEnvFactory
{
    public ReadOnlyTxProcessingEnvFactory(
        IWorldStateManager worldStateManager,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ILogManager logManager,
        IPrecompileChecker precompileChecker)
        : this(worldStateManager, blockTree.AsReadOnly(), specProvider, logManager, precompileChecker)
    {
    }

    public IReadOnlyTxProcessorSource Create()
    {
        return new ReadOnlyTxProcessingEnv(
            worldStateManager,
            readOnlyBlockTree,
            specProvider,
            logManager,
            precompileChecker);
    }

    public IReadOnlyTxProcessorSource CreateForWarmingUp(IWorldState worldStateToWarmUp)
    {
        return new ReadOnlyTxProcessingEnv(
            worldStateManager,
            readOnlyBlockTree,
            specProvider,
            logManager,
            worldStateToWarmUp,
            precompileChecker);
    }
}
