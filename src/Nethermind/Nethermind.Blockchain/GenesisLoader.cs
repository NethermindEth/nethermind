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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;

namespace Nethermind.Blockchain
{
    public class GenesisLoader
    {
        private readonly ChainSpec _chainSpec;
        private readonly ISpecProvider _specProvider;
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ITransactionProcessor _transactionProcessor;

        public GenesisLoader(
            ChainSpec chainSpec,
            ISpecProvider specProvider,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ITransactionProcessor transactionProcessor)
        {
            _chainSpec = chainSpec ?? throw new ArgumentNullException(nameof(chainSpec));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));
        }
        
        public Block Load()
        {
            Block genesis = _chainSpec.Genesis;
            Preallocate(genesis);
            
            // we no longer need the allocations - 0.5MB RAM, 9000 objects for mainnet
            _chainSpec.Allocations = null;

            _storageProvider.Commit();
            _stateProvider.Commit(_specProvider.GenesisSpec, true);

            _storageProvider.CommitTrees(0);
            _stateProvider.CommitTree(0);

            genesis.Header.StateRoot = _stateProvider.StateRoot;
            genesis.Header.Hash = genesis.Header.CalculateHash();
            
            return genesis;
        }

        private void Preallocate(Block genesis)
        {
            foreach ((Address address, ChainSpecAllocation allocation) in _chainSpec.Allocations.OrderBy(a => a.Key))
            {
                _stateProvider.CreateAccount(address, allocation.Balance, allocation.Nonce);

                if (allocation.Code != null)
                {
                    Keccak codeHash = _stateProvider.UpdateCode(allocation.Code);
                    _stateProvider.UpdateCodeHash(address, codeHash, _specProvider.GenesisSpec, true);
                }

                if (allocation.Storage != null)
                {
                    foreach (KeyValuePair<UInt256, byte[]> storage in allocation.Storage)
                    {
                        _storageProvider.Set(new StorageCell(address, storage.Key),
                            storage.Value.WithoutLeadingZeros().ToArray());
                    }
                }

                if (allocation.Constructor != null)
                {
                    Transaction constructorTransaction = new SystemTransaction()
                    {
                        SenderAddress = address,
                        Data = allocation.Constructor,
                        GasLimit = genesis.GasLimit
                    };

                    CallOutputTracer outputTracer = new();
                    _transactionProcessor.Execute(constructorTransaction, genesis.Header, outputTracer);

                    if (outputTracer.StatusCode != StatusCode.Success)
                    {
                        throw new InvalidOperationException(
                            $"Failed to initialize constructor for address {address}. Error: {outputTracer.Error}");
                    }
                }
            }
        }
    }
}
