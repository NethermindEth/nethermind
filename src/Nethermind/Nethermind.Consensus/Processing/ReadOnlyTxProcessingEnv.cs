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
        public IStateReader StateReader { get; }
        protected IWorldState StateProvider { get; }
        protected IBlockTree BlockTree { get; }
        public IBlockhashProvider BlockhashProvider { get; }
        protected ISpecProvider SpecProvider { get; }
        protected ILogManager LogManager { get; }

        protected ITransactionProcessor? _transactionProcessor;
        protected ITransactionProcessor TransactionProcessor
        {
            get
            {
                return _transactionProcessor ??= CreateTransactionProcessor();
            }
        }

        public IVirtualMachine Machine { get; }

        public ICodeInfoRepository CodeInfoRepository { get; }

        public ReadOnlyTxProcessingEnv(
            IWorldStateManager worldStateManager,
            IReadOnlyBlockTree readOnlyBlockTree,
            ISpecProvider specProvider,
            ILogManager logManager,
            IWorldState worldStateToWarmUp
            ) : this(worldStateManager.GlobalStateReader, worldStateManager.CreateResettableWorldState(worldStateToWarmUp), new CodeInfoRepository((worldStateToWarmUp as IPreBlockCaches)?.Caches.PrecompileCache), readOnlyBlockTree, specProvider, logManager)
        {
        }

        public ReadOnlyTxProcessingEnv(
            IWorldStateManager worldStateManager,
            IReadOnlyBlockTree readOnlyBlockTree,
            ISpecProvider specProvider,
            ILogManager logManager
            ) : this(worldStateManager.GlobalStateReader, worldStateManager.CreateResettableWorldState(), new CodeInfoRepository(), readOnlyBlockTree, specProvider, logManager)
        {
        }

        private ReadOnlyTxProcessingEnv(
            IStateReader stateReader,
            IWorldState stateProvider,
            ICodeInfoRepository codeInfoRepository,
            IReadOnlyBlockTree readOnlyBlockTree,
            ISpecProvider specProvider,
            ILogManager logManager
            )
        {
            SpecProvider = specProvider;
            StateReader = stateReader;
            StateProvider = stateProvider;
            BlockTree = readOnlyBlockTree;
            BlockhashProvider = new BlockhashProvider(BlockTree, specProvider, StateProvider, logManager);

            CodeInfoRepository = codeInfoRepository;
            Machine = new VirtualMachine(BlockhashProvider, specProvider, CodeInfoRepository, logManager);
            BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
            BlockhashProvider = new BlockhashProvider(BlockTree, specProvider, StateProvider, logManager);

            LogManager = logManager;
        }

        protected virtual ITransactionProcessor CreateTransactionProcessor() =>
            new TransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);

        public IReadOnlyTxProcessingScope Build(Hash256 stateRoot)
        {
            StateProvider.StateRoot = stateRoot;
            return new ReadOnlyTxProcessingScope(TransactionProcessor, StateProvider);
        }
    }
}
