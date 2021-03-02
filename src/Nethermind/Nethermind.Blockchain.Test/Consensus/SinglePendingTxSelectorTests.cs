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
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    public class SinglePendingTxSelectorTests
    {
        private BlockHeader _anyParent = Build.A.BlockHeader.TestObject;

        [Test]
        public void To_string_does_not_throw()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            SinglePendingTxSelector selector = new SinglePendingTxSelector(txSource);
            _ = selector.ToString();
        }
        
        [Test]
        public void Throws_on_null_argument()
        {
            Assert.Throws<ArgumentNullException>(() => new SinglePendingTxSelector(null));
        }
        
        [Test]
        public void When_no_transactions_returns_empty_list()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            SinglePendingTxSelector selector = new SinglePendingTxSelector(txSource);
            selector.GetTransactions(_anyParent, 1000000).Should().HaveCount(0);
        }

        [Test]
        public void When_many_transactions_returns_one_with_lowest_nonce_and_highest_timestamp()
        {
            ITxSource txSource = Substitute.For<ITxSource>();
            txSource.GetTransactions(_anyParent, 1000000).ReturnsForAnyArgs(new []
            {
                Build.A.Transaction.WithNonce(6).TestObject,
                Build.A.Transaction.WithNonce(1).WithTimestamp(7).TestObject,
                Build.A.Transaction.WithNonce(9).TestObject,
                Build.A.Transaction.WithNonce(1).WithTimestamp(8).TestObject,
            });

            SinglePendingTxSelector selector = new SinglePendingTxSelector(txSource);
            var result = selector.GetTransactions(_anyParent, 1000000).ToArray();
            result.Should().HaveCount(1);
            result[0].Timestamp.Should().Be(8);
            result[0].Nonce.Should().Be(1);
        }
    }
}
