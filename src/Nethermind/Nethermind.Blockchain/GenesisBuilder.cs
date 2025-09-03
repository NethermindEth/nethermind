// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Blockchain;

public class GenesisBuilder(
    ChainSpec chainSpec,
    ISpecProvider specProvider,
    IWorldState stateProvider,
    ITransactionProcessor transactionProcessor,
    IGenesisPostProcessor[] postProcessors
) : IGenesisBuilder
{

    public Block Build()
    {
        Block genesis = chainSpec.Genesis;
        Preallocate(genesis);

        // we no longer need the allocations - 0.5MB RAM, 9000 objects for mainnet
        chainSpec.Allocations = null;

        if (!chainSpec.GenesisStateUnavailable)
        {
            foreach (IGenesisPostProcessor postProcessor in postProcessors)
            {
                postProcessor.PostProcess(genesis);
            }

            stateProvider.Commit(specProvider.GenesisSpec, true);
            stateProvider.CommitTree(0);

            genesis.Header.StateRoot = stateProvider.StateRoot;
        }
        else
        {
            foreach (IGenesisPostProcessor postProcessor in postProcessors)
            {
                postProcessor.PostProcess(genesis);
            }
        }

        genesis.Header.Hash = genesis.Header.CalculateHash();

        return genesis;
    }

    private void Preallocate(Block genesis)
    {
        transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(genesis.Header, specProvider.GetSpec(genesis.Header)));
        foreach ((Address address, ChainSpecAllocation allocation) in chainSpec.Allocations.OrderBy(static a => a.Key))
        {
            stateProvider.CreateAccount(address, allocation.Balance, allocation.Nonce);

            if (allocation.Code is not null)
            {
                stateProvider.InsertCode(address, allocation.Code, specProvider.GenesisSpec, true);
            }

            if (allocation.Storage is not null)
            {
                foreach (KeyValuePair<UInt256, byte[]> storage in allocation.Storage)
                {
                    stateProvider.Set(new StorageCell(address, storage.Key),
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
                transactionProcessor.Execute(constructorTransaction, outputTracer);

                if (outputTracer.StatusCode != StatusCode.Success)
                {
                    throw new InvalidOperationException(
                        $"Failed to initialize constructor for address {address}. Error: {outputTracer.Error}");
                }
            }
        }
    }
}
