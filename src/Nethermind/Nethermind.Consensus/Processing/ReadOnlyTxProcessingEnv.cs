// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace Nethermind.Consensus.Processing;

public class ReadOnlyTxProcessingEnv : IReadOnlyTxProcessorSource
{
    protected readonly ISpecProvider _specProvider;
    protected readonly ILogManager? _logManager;

    public IStateReader StateReader { get; }
    public IWorldState StateProvider { get; }
    public ITransactionProcessor TransactionProcessor { get; set; }
    public IBlockTree BlockTree { get; }

    public ReadOnlyTxProcessingEnv(
      IWorldStateManager? worldStateManager,
      IReadOnlyBlockTree? readOnlyBlockTree,
      ISpecProvider? specProvider,
      ILogManager? logManager,
      PreBlockCaches? preBlockCaches = null)
    {
        ArgumentNullException.ThrowIfNull(worldStateManager);
        ArgumentNullException.ThrowIfNull(readOnlyBlockTree);
        ArgumentNullException.ThrowIfNull(specProvider);

        StateReader = worldStateManager.GlobalStateReader;
        StateProvider = worldStateManager.CreateResettableWorldState(preBlockCaches);
        BlockTree = readOnlyBlockTree;
        _specProvider = specProvider;
        _logManager = logManager;
        TransactionProcessor = CreateTransactionProcessor();
    }

    protected virtual TransactionProcessor CreateTransactionProcessor()
    {
        BlockhashProvider blockhashProvider = new(BlockTree, _specProvider, StateProvider, _logManager);
        VirtualMachine virtualMachine = new(blockhashProvider, _specProvider, _logManager);
        return new TransactionProcessor(_specProvider, StateProvider, virtualMachine, _logManager);
    }

    public IReadOnlyTransactionProcessor Build(Hash256 stateRoot) => new ReadOnlyTransactionProcessor(TransactionProcessor, StateProvider, stateRoot);

    public void Reset()
    {
        StateProvider.Reset();
    }
}
