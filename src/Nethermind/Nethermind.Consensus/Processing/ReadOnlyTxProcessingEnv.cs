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
        public IWorldState StateProvider { get; }
        public ITransactionProcessor TransactionProcessor { get; set; }
        public IBlockTree BlockTree { get; }
        public IBlockhashProvider BlockhashProvider { get; }
        public IVirtualMachine Machine { get; }
        public Action ResetDb { get; }

        public ReadOnlyTxProcessingEnv(
            IWorldStateFactory worldStateFactory,
            IBlockTree? blockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager)
            : this(worldStateFactory, blockTree?.AsReadOnly(), specProvider, logManager)
        {
        }

        public ReadOnlyTxProcessingEnv(
            IWorldStateFactory worldStateFactory,
            IReadOnlyBlockTree? readOnlyBlockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager)
        {
            if (specProvider is null) throw new ArgumentNullException(nameof(specProvider));
            if (worldStateFactory is null) throw new ArgumentNullException(nameof(worldStateFactory));

            (IWorldState worldState, IStateReader stateReader, Action reset) = worldStateFactory.CreateResettableWorldState();
            StateReader = stateReader;
            StateProvider = worldState;
            ResetDb = reset;

            BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
            BlockhashProvider = new BlockhashProvider(BlockTree, logManager);

            Machine = new VirtualMachine(BlockhashProvider, specProvider, logManager);
            TransactionProcessor = new TransactionProcessor(specProvider, StateProvider, Machine, logManager);
        }

        public IReadOnlyTransactionProcessor Build(Hash256 stateRoot) => new ReadOnlyTransactionProcessor(TransactionProcessor, StateProvider, stateRoot);
    }
}
