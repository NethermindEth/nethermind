// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class OverridableTxProcessingEnv : ReadOnlyTxProcessingEnvBase, IReadOnlyTxProcessorSource, IOverridableTxProcessorSource
{
    protected new OverridableWorldState StateProvider { get; }
    protected OverridableWorldStateManager WorldStateManager { get; }
    protected OverridableCodeInfoRepository CodeInfoRepository { get; }
    protected IVirtualMachine Machine { get; }
    protected ITransactionProcessor TransactionProcessor { get; init; }

    public OverridableTxProcessingEnv(
        OverridableWorldStateManager worldStateManager,
        IReadOnlyBlockTree readOnlyBlockTree,
        ISpecProvider specProvider,
        ILogManager? logManager,
        IWorldState? worldStateToWarmUp = null
    ) : base(worldStateManager, readOnlyBlockTree, specProvider, logManager, worldStateToWarmUp)
    {
        WorldStateManager = worldStateManager;
        StateProvider = (OverridableWorldState)base.StateProvider;
        CodeInfoRepository = new(new CodeInfoRepository((worldStateToWarmUp as IPreBlockCaches)?.Caches.PrecompileCache));
        Machine = new VirtualMachine(BlockhashProvider, specProvider, CodeInfoRepository, logManager);
        TransactionProcessor = new TransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);
    }

    IReadOnlyTxProcessingScope IReadOnlyTxProcessorSource.Build(Hash256 stateRoot) => Build(stateRoot);

    IOverridableTxProcessingScope IOverridableTxProcessorSource.Build(Hash256 stateRoot) => Build(stateRoot);

    public OverridableTxProcessingScope Build(Hash256 stateRoot)
    {
        Hash256 originalStateRoot = StateProvider.StateRoot;
        StateProvider.StateRoot = stateRoot;
        return new(CodeInfoRepository, TransactionProcessor, StateProvider, originalStateRoot);
    }
}
