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

public class OverridableTxProcessingEnv : IOverridableTxProcessorSource
{
    public IStateReader StateReader { get; }
    public IBlockTree BlockTree { get; }
    protected ISpecProvider SpecProvider { get; }
    protected ILogManager LogManager { get; }

    private readonly Lazy<ITransactionProcessor> _transactionProcessorLazy;
    protected IOverridableWorldState StateProvider { get; }
    protected OverridableCodeInfoRepository CodeInfoRepository { get; }
    protected IVirtualMachine Machine { get; }
    protected ITransactionProcessor TransactionProcessor => _transactionProcessorLazy.Value;

    public OverridableTxProcessingEnv(
        IOverridableWorldScope overridableScope,
        IReadOnlyBlockTree readOnlyBlockTree,
        ISpecProvider specProvider,
        ILogManager? logManager
    )
    {
        SpecProvider = specProvider;
        StateReader = overridableScope.GlobalStateReader;
        BlockTree = readOnlyBlockTree;
        IBlockhashProvider blockhashProvider = new BlockhashProvider(BlockTree, specProvider, StateProvider, logManager);
        LogManager = logManager;
        StateProvider = overridableScope.WorldState;

        CodeInfoRepository = new(new CodeInfoRepository());
        Machine = new VirtualMachine(blockhashProvider, specProvider, logManager);
        _transactionProcessorLazy = new(CreateTransactionProcessor);
    }

    protected virtual ITransactionProcessor CreateTransactionProcessor() =>
        new TransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);

    IOverridableTxProcessingScope IOverridableTxProcessorSource.Build(Hash256 stateRoot) => Build(stateRoot);

    public OverridableTxProcessingScope Build(Hash256 stateRoot) => new(CodeInfoRepository, TransactionProcessor, StateProvider, stateRoot);

    IOverridableTxProcessingScope IOverridableTxProcessorSource.BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride)
    {
        OverridableTxProcessingScope scope = Build(header.StateRoot ?? throw new ArgumentException($"Block {header.Hash} state root is null", nameof(header)));
        if (stateOverride is not null)
        {
            scope.WorldState.ApplyStateOverrides(scope.CodeInfoRepository, stateOverride, SpecProvider.GetSpec(header), header.Number);
            header.StateRoot = scope.WorldState.StateRoot;
        }
        return scope;
    }
}
