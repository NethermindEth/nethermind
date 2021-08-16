//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.Processing
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
            DbProvider = readOnlyDbProvider ?? throw new ArgumentNullException(nameof(readOnlyDbProvider));
            _codeDb = readOnlyDbProvider.CodeDb.AsReadOnly(true);

            StateReader = new StateReader(readOnlyTrieStore, _codeDb, logManager);
            StateProvider = new StateProvider(readOnlyTrieStore, _codeDb, logManager);
            StorageProvider = new StorageProvider(readOnlyTrieStore, StateProvider, logManager);

            BlockTree = readOnlyBlockTree ?? throw new ArgumentNullException(nameof(readOnlyBlockTree));
            BlockhashProvider = new BlockhashProvider(BlockTree, logManager);

            Machine = new VirtualMachine(StateProvider, StorageProvider, BlockhashProvider, specProvider, logManager);
            TransactionProcessor = new TransactionProcessor(specProvider, StateProvider, StorageProvider, Machine, logManager);
        }

        public void Reset()
        {
            StateProvider.Reset();
            StorageProvider.Reset();
            
            _codeDb.ClearTempChanges();
        }

        public IReadOnlyTransactionProcessor Build(Keccak stateRoot) => new ReadOnlyTransactionProcessor(TransactionProcessor, StateProvider, StorageProvider, stateRoot);
    }
}
