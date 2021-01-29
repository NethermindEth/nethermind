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

using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.Processing
{
    public class ReadOnlyTxProcessingEnv
    {
        public IStateReader StateReader { get; }
        public IStateProvider StateProvider { get; }
        public IStorageProvider StorageProvider { get; }
        public ITransactionProcessor TransactionProcessor { get; set; }
        public IBlockTree BlockTree { get; }
        public IReadOnlyDbProvider DbProvider { get; }

        private IBlockhashProvider BlockhashProvider;
        private IVirtualMachine VirtualMachine;
        
        public ReadOnlyTxProcessingEnv(
            IReadOnlyDbProvider readOnlyDbProvider,
            ITrieNodeResolver trieStore,
            ReadOnlyBlockTree readOnlyBlockTree,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            // TODO: reuse cache for state -> now when the trie is not reusing cache any more
            DbProvider = readOnlyDbProvider;
            IDb codeDb = readOnlyDbProvider.CodeDb;
            
            ReadOnlyTrieStore readOnlyTrieStore = new ReadOnlyTrieStore(trieStore);
            StateReader = new StateReader(readOnlyTrieStore, codeDb, logManager);
            StateProvider = new StateProvider(readOnlyTrieStore, codeDb, logManager);
            StorageProvider = new StorageProvider(readOnlyTrieStore, StateProvider, logManager);

            BlockTree = readOnlyBlockTree;
            BlockhashProvider = new BlockhashProvider(BlockTree, logManager);

            VirtualMachine = new VirtualMachine(StateProvider, StorageProvider, BlockhashProvider, specProvider, logManager);
            TransactionProcessor = new TransactionProcessor(specProvider, StateProvider, StorageProvider, VirtualMachine, logManager);
        }

        public void Reset()
        {
            StateProvider.Reset();
            StorageProvider.Reset();
        }
    }
}
