// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus
{
    public record BlockProducerEnv(
        IBlockTree BlockTree,
        IBlockchainProcessor ChainProcessor,
        IWorldState ReadOnlyStateProvider,
        ITxSource TxSource,
        IPrefetchManager? PrefetchManager = null) : IBlockProducerEnv;

    public interface IBlockProducerEnv
    {
        public IBlockTree BlockTree { get; }
        public IBlockchainProcessor ChainProcessor { get; }
        public IWorldState ReadOnlyStateProvider { get; }
        public ITxSource TxSource { get; }
        public IPrefetchManager? PrefetchManager { get; }
    }

    public interface IPrefetchManager
    {
        void PrefetchBlock(Block preWarmBlock, BlockHeader parentHeader, IReleaseSpec releaseSpec);
    }
}
