// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
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
using Nethermind.Xdc.Spec;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc;

public class XdcGenesisBuilder(
    ChainSpec chainSpec,
    ISpecProvider specProvider,
    IWorldState stateProvider,
    ITransactionProcessor transactionProcessor,
    ISnapshotManager snapshotManager,
    params IGenesisPostProcessor[] postProcessors
) : IGenesisBuilder
{
    public Block Build()
    {
        Block genesis = chainSpec.Genesis;
        genesis = genesis.WithReplacedHeader(XdcBlockHeader.FromBlockHeader(genesis.Header));
        Preallocate(genesis);

        foreach (IGenesisPostProcessor postProcessor in postProcessors)
        {
            postProcessor.PostProcess(genesis);
        }

        stateProvider.Commit(specProvider.GenesisSpec, true);
        stateProvider.CommitTree(0);
        genesis.Header.StateRoot = stateProvider.StateRoot;
        genesis.Header.Hash = genesis.Header.CalculateHash();

        var finalSpec = (IXdcReleaseSpec)specProvider.GetFinalSpec();
        snapshotManager.StoreSnapshot(new Types.Snapshot(genesis.Number, genesis.Hash!, finalSpec.GenesisMasterNodes));

        return genesis;
    }
    private void Preallocate(Block genesis)
    {
        // Use shared preallocation logic from GenesisBuilder to avoid code duplication
        GenesisBuilder.PreallocateAccounts(genesis, chainSpec, specProvider, stateProvider, transactionProcessor);
    }
}
