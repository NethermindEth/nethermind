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
    public class ReadOnlyTxProcessingEnv : ReadOnlyTxProcessingEnvBase, IReadOnlyTxProcessorSource
    {
        public ITransactionProcessor TransactionProcessor { get; set; }
        public IVirtualMachine Machine { get; }

        public ICodeInfoRepository CodeInfoRepository { get; }
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
            ILogManager? logManager
            ) : base(worldStateManager, readOnlyBlockTree, logManager)
        {
            //TO DO CHECK AGAIN!!!
            ArgumentNullException.ThrowIfNull(specProvider);
            ArgumentNullException.ThrowIfNull(worldStateManager);

            CodeInfoRepository = new CodeInfoRepository();
            Machine = new VirtualMachine(BlockHashProvider, specProvider, CodeInfoRepository, logManager);
            TransactionProcessor = new TransactionProcessor(specProvider, StateProvider, Machine, CodeInfoRepository, logManager);
        }

        public IReadOnlyTransactionProcessor Build(Hash256 stateRoot) => new ReadOnlyTransactionProcessor(TransactionProcessor, StateProvider, stateRoot);
    }
}
