// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace Nethermind.Consensus.Processing
{
    public class ReadOnlyTxProcessingEnv : IReadOnlyTxProcessorSource
    {
        private readonly ReadOnlyDb _codeDb;
        public IStateReader StateReader { get; }
        public IStateProvider StateProvider { get; }
        public IStorageProvider StorageProvider { get; }
        public ITransactionProcessor TransactionProcessor { get; set; }
        public IBlockTree BlockTree { get; }
        public IReadOnlyDbProvider DbProvider { get; }
        public IBlockhashProvider BlockhashProvider { get; }
        public IVirtualMachine Machine { get; }

        public ReadOnlyTxProcessingEnv(
            IDbProvider? dbProvider,
            IReadOnlyTrieStore? trieStore,
            IBlockTree? blockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager)
            : this(dbProvider?.AsReadOnly(false), trieStore, blockTree?.AsReadOnly(), specProvider, logManager)
        {
        }

        public ReadOnlyTxProcessingEnv(
            IReadOnlyDbProvider? readOnlyDbProvider,
            IReadOnlyTrieStore? readOnlyTrieStore,
            IReadOnlyBlockTree? readOnlyBlockTree,
            ISpecProvider? specProvider,
            ILogManager? logManager)
        {
            if (specProvider is null) throw new ArgumentNullException(nameof(specProvider));

            DbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
            _codeDb = readOnlyDbProvider.CodeDb.AsReadOnly(true);

            StateReader = new StateReader(readOnlyTrieStore, _codeDb, logManager);
            StateProvider = new StateProvider(readOnlyTrieStore, _codeDb, logManager);
            StorageProvider = new StorageProvider(readOnlyTrieStore, StateProvider, logManager);
            IWorldState worldState = new WorldState(StateProvider, StorageProvider);

            BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
            BlockhashProvider = new BlockhashProvider(BlockTree, logManager);

            Machine = new VirtualMachine(BlockhashProvider, specProvider, logManager);
            TransactionProcessor = new TransactionProcessor(specProvider, worldState, Machine, BlockTree, logManager);
        }

        public IReadOnlyTransactionProcessor Build(Keccak stateRoot) => new ReadOnlyTransactionProcessor(TransactionProcessor, StateProvider, StorageProvider, _codeDb, stateRoot);
    }
}
