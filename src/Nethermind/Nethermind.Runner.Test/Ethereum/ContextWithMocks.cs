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
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Db.Blooms;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;

namespace Nethermind.Runner.Test.Ethereum
{
    public static class Build
    {
        public static EthereumRunnerContext ContextWithMocks() =>
            new EthereumRunnerContext(Substitute.For<IConfigProvider>(), LimboLogs.Instance)
            {
                LogManager = LimboLogs.Instance,
                Enode = Substitute.For<IEnode>(),
                TxPool = Substitute.For<ITxPool>(),
                Wallet = Substitute.For<IWallet>(),
                BlockTree = Substitute.For<IBlockTree>(),
                SyncServer = Substitute.For<ISyncServer>(),
                DbProvider = Substitute.For<IDbProvider>(),
                PeerManager = Substitute.For<IPeerManager>(),
                SpecProvider = Substitute.For<ISpecProvider>(),
                EthereumEcdsa = Substitute.For<IEthereumEcdsa>(),
                MainBlockProcessor = Substitute.For<IBlockProcessor>(),
                ReceiptStorage = Substitute.For<IReceiptStorage>(),
                ReceiptFinder = Substitute.For<IReceiptFinder>(),
                BlockValidator = Substitute.For<IBlockValidator>(),
                RewardCalculatorSource = Substitute.For<IRewardCalculatorSource>(),
                RecoveryStep = Substitute.For<IBlockDataRecoveryStep>(),
                TxPoolInfoProvider = Substitute.For<ITxPoolInfoProvider>(),
                StaticNodesManager = Substitute.For<IStaticNodesManager>(),
                BloomStorage = Substitute.For<IBloomStorage>()
            };
    }
}