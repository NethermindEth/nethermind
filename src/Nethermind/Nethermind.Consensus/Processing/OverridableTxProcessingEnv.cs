// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class OverridableTxProcessingEnv : ReadOnlyTxProcessingEnvBase, IOverridableTxProcessorSource
{
    private readonly Lazy<ITransactionProcessor> _transactionProcessorLazy;

    protected new OverridableWorldState StateProvider { get; }
    protected OverridableWorldStateManager WorldStateManager { get; }
    protected OverridableCodeInfoRepository CodeInfoRepository { get; }
    protected IVirtualMachine Machine { get; }
    protected ITransactionProcessor TransactionProcessor => _transactionProcessorLazy.Value;

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
        _transactionProcessorLazy = new(CreateTransactionProcessor);
    }


    protected virtual ITransactionProcessor CreateTransactionProcessor() =>
        new TransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);

    IOverridableTxProcessingScope IOverridableTxProcessorSource.Build(Hash256 stateRoot) => Build(stateRoot);

    public OverridableTxProcessingScope Build(Hash256 stateRoot)
    {
        Hash256 originalStateRoot = StateProvider.StateRoot;
        StateProvider.StateRoot = stateRoot;
        return new(CodeInfoRepository, TransactionProcessor, StateProvider, originalStateRoot);
    }

    IOverridableTxProcessingScope IOverridableTxProcessorSource.BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride)
    {
        OverridableTxProcessingScope scope = Build(header.StateRoot ?? throw new ArgumentException($"Block {header.Hash} state root is null", nameof(header)));
        if (stateOverride != null)
        {
            scope.WorldState.ApplyStateOverrides(scope.CodeInfoRepository, stateOverride, SpecProvider.GetSpec(header), header.Number);
            header.StateRoot = scope.WorldState.StateRoot;
        }
        return scope;
    }
}
