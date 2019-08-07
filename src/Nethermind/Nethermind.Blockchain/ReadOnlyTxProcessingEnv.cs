/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class ReadOnlyTxProcessingEnv
    {
        public IStateReader StateReader;
        public IStateProvider StateProvider;
        public IStorageProvider StorageProvider;
        public IBlockhashProvider BlockhashProvider;
        public IVirtualMachine VirtualMachine;
        public TransactionProcessor TransactionProcessor;
        public IBlockTree BlockTree;

        public ReadOnlyTxProcessingEnv(IReadOnlyDbProvider readOnlyDbProvider, ReadOnlyBlockTree readOnlyBlockTree, ISpecProvider specProvider, ILogManager logManager)
        {
            ISnapshotableDb stateDb = readOnlyDbProvider.StateDb;
            IDb codeDb = readOnlyDbProvider.CodeDb;

            StateReader = new StateReader(stateDb, codeDb, logManager);
            StateProvider = new StateProvider(stateDb, codeDb, logManager);
            StorageProvider = new StorageProvider(stateDb, StateProvider, logManager);

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