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
namespace Nethermind.Consensus.Processing
{
    public class ReadOnlyTxProcessingEnv : IReadOnlyTxProcessorSource
    {
        protected readonly ILogManager _logManager;
        public IStateReader StateReader { get; }
        protected IWorldState StateProvider { get; }

        protected ITransactionProcessor? _transactionProcessor;
        protected ITransactionProcessor TransactionProcessor
        {
            get
            {
                return _transactionProcessor ??= CreateTransactionProcessor();
            }
        }

        public IBlockTree BlockTree { get; }
        public IBlockhashProvider BlockhashProvider { get; }
        public IVirtualMachine Machine { get; }
        public ISpecProvider SpecProvider { get; }

        public ReadOnlyTxProcessingEnv(
            IWorldStateManager worldStateManager,
            IBlockTree? blockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager)
            : this(worldStateManager, blockTree?.AsReadOnly(), specProvider, logManager)
        {
        }

        public ReadOnlyTxProcessingEnv(
            IWorldStateManager worldStateManager,
            IReadOnlyBlockTree? readOnlyBlockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager,
            PreBlockCaches? preBlockCaches = null)
        {
            ArgumentNullException.ThrowIfNull(specProvider);
            ArgumentNullException.ThrowIfNull(worldStateManager);
            SpecProvider = specProvider;
            StateReader = worldStateManager.GlobalStateReader;
            StateProvider = worldStateManager.CreateResettableWorldState(preBlockCaches);

            BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
            BlockhashProvider = new BlockhashProvider(BlockTree, specProvider, StateProvider, logManager);

            Machine = new VirtualMachine(BlockhashProvider, specProvider, logManager);
            _logManager = logManager;
        }

        protected virtual TransactionProcessor CreateTransactionProcessor()
        {
            return new TransactionProcessor(SpecProvider, StateProvider, Machine, _logManager);
        }

        public void Reset()
        {
            StateProvider.Reset();
        }

        public IReadOnlyTxProcessingScope Build(Hash256 stateRoot)
        {
            Hash256 originalStateRoot = StateProvider.StateRoot;
            StateProvider.StateRoot = stateRoot;
            return new ReadOnlyTxProcessingScope(TransactionProcessor, StateProvider, originalStateRoot);
        }
    }
}
