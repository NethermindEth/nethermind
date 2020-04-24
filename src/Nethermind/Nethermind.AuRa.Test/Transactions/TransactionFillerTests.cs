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

using FluentAssertions;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.State;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class TransactionFillerTests
    {
        [Test]
        public void transaction_is_addable_to_block_after_fill()
        {
            int chainId = 5;
            var blockHeader = Build.A.BlockHeader.TestObject;
            var tx = Build.A.Transaction.TestObject;
            var timestamper = Substitute.For<ITimestamper>();
            var stateReader = Substitute.For<IStateReader>();
            
            UInt256 expectedNonce = 10;
            stateReader.GetNonce(blockHeader.StateRoot, tx.SenderAddress).Returns(expectedNonce);
            
            ulong expectedTimeStamp = 100;
            timestamper.EpochSeconds.Returns(expectedTimeStamp);
            
            var transactionFiller = new TransactionFiller(new BasicWallet(Build.A.PrivateKey.TestObject), timestamper, stateReader, chainId);
            
            transactionFiller.Fill(blockHeader, tx);

            tx.IsSigned.Should().BeTrue();
            tx.Nonce.Should().Be(expectedNonce);
            tx.Hash.Should().Be(tx.CalculateHash());
            tx.Timestamp.Should().Be(expectedTimeStamp);

        }
    }
}