//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class GeneratedTxSourceSealerTests
    {
        [Test]
        public void transactions_are_addable_to_block_after_sealing()
        {
            int chainId = 5;
            var blockHeader = Build.A.BlockHeader.TestObject;
            var tx1 = Build.A.GeneratedTransaction.WithSenderAddress(TestItem.AddressA).TestObject;
            var tx2 = Build.A.GeneratedTransaction.WithSenderAddress(TestItem.AddressA).TestObject;
            var timestamper = Substitute.For<ITimestamper>();
            var stateReader = Substitute.For<IStateReader>();
            var nodeAddress = TestItem.AddressA;
            
            UInt256 expectedNonce = 10;
            stateReader.GetAccount(blockHeader.StateRoot, nodeAddress).Returns(Account.TotallyEmpty.WithChangedNonce(expectedNonce));
            
            ulong expectedTimeStamp = 100;
            timestamper.UnixTime.Returns(UnixTime.FromSeconds(expectedTimeStamp));

            var gasLimit = 200;
            var innerTxSource = Substitute.For<ITxSource>();
            innerTxSource.GetTransactions(blockHeader, gasLimit).Returns(new[] {tx1, tx2});
            
            TxSealer txSealer = new TxSealer(new Signer((ulong) chainId, Build.A.PrivateKey.TestObject, LimboLogs.Instance), timestamper);
            var transactionFiller = new GeneratedTxSource(innerTxSource, txSealer, stateReader, LimboLogs.Instance);

            var sealedTxs = transactionFiller.GetTransactions(blockHeader, gasLimit).ToArray();
            var sealedTx1 = sealedTxs.First();
            var sealedTx2 = sealedTxs.Skip(1).First();
            
            sealedTx1.IsSigned.Should().BeTrue();
            sealedTx1.Nonce.Should().Be(expectedNonce);
            sealedTx1.Hash.Should().Be(tx1.CalculateHash());
            sealedTx1.Timestamp.Should().Be(expectedTimeStamp);
            
            sealedTx2.IsSigned.Should().BeTrue();
            sealedTx2.Nonce.Should().Be(expectedNonce + 1);
            sealedTx2.Hash.Should().NotBe(tx1.CalculateHash());
            sealedTx2.Timestamp.Should().Be(expectedTimeStamp);

        }
    }
}
