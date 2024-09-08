// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
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
        public ITransactionProcessor TransactionProcessor
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
            ILogManager? logManager)
            : this(worldStateManager, blockTree.AsReadOnly(), specProvider, logManager)
        {
        }

        public ReadOnlyTxProcessingEnv(
            IWorldStateManager worldStateManager,
            IReadOnlyBlockTree readOnlyBlockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager
            ) : base(worldStateManager, readOnlyBlockTree, specProvider, logManager)
        {
            CodeInfoRepository = new CodeInfoRepository(worldStateManager.Caches?.PrecompileCache);
            Machine = new VirtualMachine(BlockhashProvider, specProvider, CodeInfoRepository, logManager);
            BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
            BlockhashProvider = new BlockhashProvider(BlockTree, specProvider, logManager);

            _logManager = logManager;
        }

        protected virtual TransactionProcessor CreateTransactionProcessor()
        {
            return new TransactionProcessor(SpecProvider, Machine, CodeInfoRepository, _logManager);
        }

        public IReadOnlyTxProcessingScope Build(Hash256 stateRoot, BlockHeader header)
        {
            // TODO: really bad idea - find a better way?
            IWorldState? worldStateToUse =
                WorldStateProvider.GetGlobalWorldState(header);
            Hash256 originalStateRoot = worldStateToUse.StateRoot;
            worldStateToUse.StateRoot = stateRoot;
            return new ReadOnlyTxProcessingScope(TransactionProcessor, worldStateToUse, originalStateRoot);
        }
    }
}
