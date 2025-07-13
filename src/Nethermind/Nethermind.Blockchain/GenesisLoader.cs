// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Blockchain
{
    public class GenesisLoader(
        ChainSpec chainSpec,
        ISpecProvider specProvider,
        IStateReader stateReader,
        IWorldState stateProvider,
        ITransactionProcessor transactionProcessor,
        ILogManager logManager,
        Hash256? expectedGenesisHash = null
    ) {
        ILogger _logger = logManager.GetClassLogger<GenesisLoader>();

        public Block Load()
        {
            Block genesis = chainSpec.Genesis;
            Preallocate(genesis);

            // we no longer need the allocations - 0.5MB RAM, 9000 objects for mainnet
            chainSpec.Allocations = null;

            if (!chainSpec.GenesisStateUnavailable)
            {
                stateProvider.Commit(specProvider.GenesisSpec, true);

                stateProvider.CommitTree(0);

                genesis.Header.StateRoot = stateProvider.StateRoot;
            }

            genesis.Header.Hash = genesis.Header.CalculateHash();

            ValidateGenesisHash(expectedGenesisHash, genesis.Header);

            return genesis;
        }

        /// <summary>
        /// If <paramref name="expectedGenesisHash"/> is <value>null</value> then it means that we do not care about the genesis hash (e.g. in some quick testing of private chains)/>
        /// </summary>
        /// <param name="expectedGenesisHash"></param>
        private void ValidateGenesisHash(Hash256? expectedGenesisHash, BlockHeader genesis)
        {
            if (expectedGenesisHash is not null && genesis.Hash != expectedGenesisHash)
            {
                if (_logger.IsTrace) _logger.Trace(stateReader.DumpState(genesis.StateRoot!));
                if (_logger.IsWarn) _logger.Warn(genesis.ToString(BlockHeader.Format.Full));
                if (_logger.IsError) _logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {genesis.Hash}");
            }
            else
            {
                if (_logger.IsDebug) _logger.Info($"Genesis hash :  {genesis.Hash}");
            }

            ThisNodeInfo.AddInfo("Genesis hash :", $"{genesis.Hash}");
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
}
