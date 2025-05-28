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
    internal sealed class ReadOnlyTxProcessingEnv : IReadOnlyTxProcessorSource
    {
        private IStateReader StateReader { get; }
        private IWorldState StateProvider { get; }
        private IBlockTree BlockTree { get; }
        private IBlockhashProvider BlockhashProvider { get; }
        private ISpecProvider SpecProvider { get; }
        private ILogManager LogManager { get; }

        private ITransactionProcessor? _transactionProcessor;

        private ITransactionProcessor TransactionProcessor
        {
            get
            {
                return _transactionProcessor ??= CreateTransactionProcessor();
            }
        }

        private IVirtualMachine Machine { get; }

        private ICodeInfoRepository CodeInfoRepository { get; }

        public ReadOnlyTxProcessingEnv(
            IWorldStateManager worldStateManager,
            IReadOnlyBlockTree readOnlyBlockTree,
            ISpecProvider specProvider,
            ILogManager logManager,
            IWorldState worldStateToWarmUp
            ) : this(worldStateManager.GlobalStateReader, worldStateManager.CreateWorldStateForWarmingUp(worldStateToWarmUp), new CodeInfoRepository((worldStateToWarmUp as IPreBlockCaches)?.Caches.PrecompileCache), readOnlyBlockTree, specProvider, logManager)
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
            Machine = new VirtualMachine(BlockhashProvider, specProvider, logManager);
            BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
            BlockhashProvider = new BlockhashProvider(BlockTree, specProvider, StateProvider, logManager);

            LogManager = logManager;
        }

        private ITransactionProcessor CreateTransactionProcessor() =>
            new TransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, LogManager);

        public IReadOnlyTxProcessingScope Build(Hash256 stateRoot)
        {
            Hash256 originalStateRoot = StateProvider.StateRoot;
            StateProvider.StateRoot = stateRoot;
            return new ReadOnlyTxProcessingScope(TransactionProcessor, StateProvider, originalStateRoot);
        }
    }
}
