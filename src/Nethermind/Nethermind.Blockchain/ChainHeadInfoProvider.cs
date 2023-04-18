// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Spec;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.TxPool;

[assembly: InternalsVisibleTo("Nethermind.TxPool.Test")]

namespace Nethermind.Blockchain
{
    public class ChainHeadInfoProvider : IChainHeadInfoProvider
    {
        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IStateReader stateReader)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, new ChainHeadReadOnlyStateProvider(blockTree, stateReader))
        {
        }

        public ChainHeadInfoProvider(ISpecProvider specProvider, IBlockTree blockTree, IAccountStateProvider stateProvider)
            : this(new ChainHeadSpecProvider(specProvider, blockTree), blockTree, stateProvider)
        {
        }

        public ChainHeadInfoProvider(IChainHeadSpecProvider specProvider, IBlockTree blockTree, IAccountStateProvider stateProvider)
        {
            SpecProvider = specProvider;
            AccountStateProvider = stateProvider;

            blockTree.BlockAddedToMain += OnHeadChanged;
        }

        public IChainHeadSpecProvider SpecProvider { get; }

        public IAccountStateProvider AccountStateProvider { get; }

        public long? BlockGasLimit { get; internal set; }

        public UInt256 CurrentBaseFee { get; private set; }

        public event EventHandler<BlockReplacementEventArgs>? HeadChanged;

        private void OnHeadChanged(object? sender, BlockReplacementEventArgs e)
        {
            BlockGasLimit = e.Block!.GasLimit;
            CurrentBaseFee = e.Block.Header.BaseFeePerGas;
            HeadChanged?.Invoke(sender, e);
        }
    }
}
