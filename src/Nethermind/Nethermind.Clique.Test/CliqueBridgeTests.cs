/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Blockchain;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    [TestFixture]
    public class CliqueBridgeTests
    {
        [Test]
        public void Sets_clique_block_producer_properly()
        {
            CliqueConfig cliqueConfig = new CliqueConfig();
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            CliqueBlockProducer producer = new CliqueBlockProducer(
                Substitute.For<ITxPool>(),
                Substitute.For<IBlockchainProcessor>(),
                blockTree,
                Substitute.For<ITimestamper>(),
                Substitute.For<ICryptoRandom>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<ISnapshotManager>(),
                new CliqueSealer(new BasicWallet(TestItem.PrivateKeyA), cliqueConfig, Substitute.For<ISnapshotManager>(), TestItem.PrivateKeyA.Address, NullLogManager.Instance),
                TestItem.AddressA,
                cliqueConfig,
                NullLogManager.Instance);
            
            SnapshotManager snapshotManager = new SnapshotManager(CliqueConfig.Default, new MemDb(), Substitute.For<IBlockTree>(), NullEthereumEcdsa.Instance, LimboLogs.Instance);
            
            CliqueBridge bridge = new CliqueBridge(producer, snapshotManager, blockTree);
            Assert.DoesNotThrow(() => bridge.CastVote(TestItem.AddressB, true));
            Assert.DoesNotThrow(() => bridge.UncastVote(TestItem.AddressB));
            Assert.DoesNotThrow(() => bridge.CastVote(TestItem.AddressB, false));
            Assert.DoesNotThrow(() => bridge.UncastVote(TestItem.AddressB));
        }
    }
}