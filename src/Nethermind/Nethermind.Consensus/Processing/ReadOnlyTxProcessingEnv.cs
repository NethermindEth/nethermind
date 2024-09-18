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
        protected readonly ILogManager _logManager;

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
            IBlockTree blockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager,
            IWorldState? worldStateToWarmUp = null)
            : this(worldStateManager, blockTree.AsReadOnly(), specProvider, logManager, worldStateToWarmUp)
        {
        }

        public ReadOnlyTxProcessingEnv(
            IWorldStateManager worldStateManager,
            IReadOnlyBlockTree readOnlyBlockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager,
            IWorldState? worldStateToWarmUp = null
            ) : base(worldStateManager, readOnlyBlockTree, specProvider, logManager, worldStateToWarmUp)
        {
            CodeInfoRepository = new CodeInfoRepository(specProvider.ChainId, (worldStateToWarmUp as IPreBlockCaches)?.Caches.PrecompileCache);
            Machine = new VirtualMachine(BlockhashProvider, specProvider, CodeInfoRepository, logManager);
            BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
            BlockhashProvider = new BlockhashProvider(BlockTree, specProvider, StateProvider, logManager);

            _logManager = logManager;
        }

        protected virtual ITransactionProcessor CreateTransactionProcessor() =>
            new TransactionProcessor(SpecProvider, StateProvider, Machine, CodeInfoRepository, _logManager);

        public IReadOnlyTxProcessingScope Build(Hash256 stateRoot)
        {
            Hash256 originalStateRoot = StateProvider.StateRoot;
            StateProvider.StateRoot = stateRoot;
            return new ReadOnlyTxProcessingScope(TransactionProcessor, StateProvider, originalStateRoot);
        }
    }
}
