// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Evm.State;

namespace Nethermind.Consensus
{
    public record BlockProducerEnv(
        IBlockTree BlockTree,
        IBlockchainProcessor ChainProcessor,
        IWorldState ReadOnlyStateProvider,
        ITxSource TxSource) : IBlockProducerEnv
    {
        public IAsyncDisposable? Scope { get; init; }

        public async ValueTask DisposeAsync()
        {
            if (Scope is not null)
                await Scope.DisposeAsync();
        }
    }

    public interface IBlockProducerEnv : IAsyncDisposable
    {
        public IBlockTree BlockTree { get; }
        public IBlockchainProcessor ChainProcessor { get; }
        public IWorldState ReadOnlyStateProvider { get; }
        public ITxSource TxSource { get; }
    }
}
