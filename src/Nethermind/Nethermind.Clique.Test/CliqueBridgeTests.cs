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
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
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
                Substitute.For<ITransactionPool>(),
                Substitute.For<IBlockchainProcessor>(),
                blockTree,
                Substitute.For<ITimestamp>(),
                Substitute.For<ICryptoRandom>(),
                Substitute.For<IStateProvider>(),
                Substitute.For<ISnapshotManager>(),
                new CliqueSealer(new BasicWallet(TestObject.PrivateKeyA), cliqueConfig, Substitute.For<ISnapshotManager>(), TestObject.PrivateKeyA.Address, NullLogManager.Instance),
                TestObject.AddressA,
                cliqueConfig,
                NullLogManager.Instance);
            
            CliqueBridge bridge = new CliqueBridge(producer, blockTree);
            Assert.DoesNotThrow(() => bridge.CastVote(TestObject.AddressB, true));
            Assert.DoesNotThrow(() => bridge.UncastVote(TestObject.AddressB));
            Assert.DoesNotThrow(() => bridge.CastVote(TestObject.AddressB, false));
            Assert.DoesNotThrow(() => bridge.UncastVote(TestObject.AddressB));
        }
    }
}