//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Bloom;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Store;
using Nethermind.Wallet;
using NSubstitute;

namespace Nethermind.Runner.Test.Ethereum
{
    public static class Build
    {
        public static EthereumRunnerContext ContextWithMocks()
        {
            EthereumRunnerContext context = new EthereumRunnerContext(Substitute.For<IConfigProvider>(), LimboLogs.Instance);
            context.LogManager = LimboLogs.Instance;
            context.Enode = Substitute.For<IEnode>();
            context.TxPool = Substitute.For<ITxPool>();
            context.Wallet = Substitute.For<IWallet>();
            context.BlockTree = Substitute.For<IBlockTree>();
            context.SyncServer = Substitute.For<ISyncServer>();
            context.DbProvider = Substitute.For<IDbProvider>();
            context.PeerManager = Substitute.For<IPeerManager>();
            context.SpecProvider = Substitute.For<ISpecProvider>();
            context.EthereumEcdsa = Substitute.For<IEthereumEcdsa>();
            context.MainBlockProcessor = Substitute.For<IBlockProcessor>();
            context.ReceiptStorage = Substitute.For<IReceiptStorage>();
            context.BlockValidator = Substitute.For<IBlockValidator>();
            context.RewardCalculatorSource = Substitute.For<IRewardCalculatorSource>();
            context.RecoveryStep = Substitute.For<IBlockDataRecoveryStep>();
            context.TxPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>();
            context.StaticNodesManager = Substitute.For<IStaticNodesManager>();
            context.BloomStorage = Substitute.For<IBloomStorage>();

            return context;
        }
    }
}