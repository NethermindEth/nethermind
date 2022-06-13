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
// 

using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Executor
{
    public class AABlockProducerTransactionsExecutorFactory : IBlockTransactionsExecutorFactory
    {
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly ISigner _signer;
        private readonly Address[] _entryPointAddresses;

        public AABlockProducerTransactionsExecutorFactory(ISpecProvider specProvider, ILogManager logManager, ISigner signer, Address[] entryPointAddresses)
        {
            _specProvider = specProvider;
            _logManager = logManager;
            _signer = signer;
            _entryPointAddresses = entryPointAddresses;
        }

        public IBlockProcessor.IBlockTransactionsExecutor Create(ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv)
            => new AABlockProducerTransactionsExecutor(
                readOnlyTxProcessingEnv.TransactionProcessor,
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                _specProvider,
                _logManager, 
                _signer, 
                _entryPointAddresses);
    }
}
