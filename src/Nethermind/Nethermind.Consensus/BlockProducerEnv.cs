// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.State;

namespace Nethermind.Consensus
{
    public record BlockProducerEnv(
        IBlockTree BlockTree,
        IBlockchainProcessor ChainProcessor,
        IWorldState ReadOnlyStateProvider,
        ITxSource TxSource) : IBlockProducerEnv;

    public interface IBlockProducerEnv
    {
        public IBlockTree BlockTree { get; }
        public IBlockchainProcessor ChainProcessor { get; }
        public IWorldState ReadOnlyStateProvider { get; }
        public ITxSource TxSource { get; }
    }
}
