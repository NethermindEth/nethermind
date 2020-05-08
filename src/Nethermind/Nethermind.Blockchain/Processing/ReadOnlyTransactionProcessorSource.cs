//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Processing
{
    public class ReadOnlyTransactionProcessorSource : IReadOnlyTransactionProcessorSource
    {
        private readonly ReadOnlyTxProcessingEnv _environment;

        public ReadOnlyTransactionProcessorSource(IDbProvider dbProvider, IBlockTree blockTree, ISpecProvider specProvider, ILogManager logManager)
        {
            _environment = new ReadOnlyTxProcessingEnv(new ReadOnlyDbProvider(dbProvider, false), new ReadOnlyBlockTree(blockTree), specProvider, logManager);
        }

        public ReadOnlyTransactionProcessorSource(ReadOnlyTxProcessingEnv environment)
        {
            _environment = environment;
        }
        
        public IReadOnlyTransactionProcessor Get(Keccak stateRoot) => new ReadOnlyTransactionProcessor(_environment.TransactionProcessor, _environment.StateProvider, stateRoot);
    }
}