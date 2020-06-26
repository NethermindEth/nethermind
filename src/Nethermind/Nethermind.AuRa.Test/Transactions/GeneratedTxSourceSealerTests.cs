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
// 

using System.Linq;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Transactions;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class GeneratedTxSourceSealerTests
    {
        [Test]
        public void transaction_is_addable_to_block_after_fill()
        {
            int chainId = 5;
            var blockHeader = Build.A.BlockHeader.TestObject;
            var tx = Build.A.GeneratedTransaction.WithSenderAddress(TestItem.AddressA).TestObject;
            var timestamper = Substitute.For<ITimestamper>();
            var stateReader = Substitute.For<IStateReader>();
            var nodeAddress = TestItem.AddressA;
            
            UInt256 expectedNonce = 10;
            stateReader.GetNonce(blockHeader.StateRoot, nodeAddress).Returns(expectedNonce);
            
            ulong expectedTimeStamp = 100;
            timestamper.EpochSeconds.Returns(expectedTimeStamp);

            var gasLimit = 200;
            var innerTxSource = Substitute.For<ITxSource>();
            innerTxSource.GetTransactions(blockHeader, gasLimit).Returns(new[] {tx});
            
            TxSealer txSealer = new TxSealer(new Signer(chainId, Build.A.PrivateKey.TestObject, LimboLogs.Instance), timestamper);
            var transactionFiller = new GeneratedTxSourceSealer(innerTxSource, txSealer, stateReader, LimboLogs.Instance);
            
            var txResult= transactionFiller.GetTransactions(blockHeader, gasLimit).First();

            txResult.IsSigned.Should().BeTrue();
            txResult.Nonce.Should().Be(expectedNonce);
            txResult.Hash.Should().Be(tx.CalculateHash());
            txResult.Timestamp.Should().Be(expectedTimeStamp);
        }
    }
}
