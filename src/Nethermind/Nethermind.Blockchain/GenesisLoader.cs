// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Blockchain
{
    public class GenesisLoader(
        ChainSpec chainSpec,
        ISpecProvider specProvider,
        IWorldState stateProvider,
        ITransactionProcessor transactionProcessor)
    {
        private readonly ChainSpec _chainSpec = chainSpec ?? throw new ArgumentNullException(nameof(chainSpec));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly IWorldState _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
        private readonly ITransactionProcessor _transactionProcessor = transactionProcessor ?? throw new ArgumentNullException(nameof(transactionProcessor));

        public Block Load()
        {
            Block genesis = _chainSpec.Genesis;
            Preallocate(genesis);

            // we no longer need the allocations - 0.5MB RAM, 9000 objects for mainnet
            _chainSpec.Allocations = null;

            if (!_chainSpec.GenesisStateUnavailable)
            {
                _stateProvider.Commit(_specProvider.GenesisSpec, true);

                _stateProvider.CommitTree(0);

                genesis.Header.StateRoot = _stateProvider.StateRoot;
            }

            genesis.Header.Hash = genesis.Header.CalculateHash();

            return genesis;
        }

        private void Preallocate(Block genesis)
        {
            _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(genesis.Header, specProvider.GetSpec(genesis.Header)));
            foreach ((Address address, ChainSpecAllocation allocation) in _chainSpec.Allocations.OrderBy(static a => a.Key))
            {
                _stateProvider.CreateAccount(address, allocation.Balance, allocation.Nonce);

                if (allocation.Code is not null)
                {
                    _stateProvider.InsertCode(address, allocation.Code, _specProvider.GenesisSpec, true);
                }

                if (allocation.Storage is not null)
                {
                    foreach (KeyValuePair<UInt256, byte[]> storage in allocation.Storage)
                    {
                        _stateProvider.Set(new StorageCell(address, storage.Key),
                            storage.Value.WithoutLeadingZeros().ToArray());
                    }
                }

                if (allocation.Constructor is not null)
                {
                    Transaction constructorTransaction = new SystemTransaction()
                    {
                        SenderAddress = address,
                        Data = allocation.Constructor,
                        GasLimit = genesis.GasLimit
                    };

                    CallOutputTracer outputTracer = new();
                    _transactionProcessor.Execute(constructorTransaction, outputTracer);

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
