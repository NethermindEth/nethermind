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

using System;
using FluentAssertions;
using System.Collections.Generic;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class TransactionTests
    {
        [Test]
        public void When_to_not_empty_then_is_message_call()
        {
            Transaction transaction = new Transaction();
            transaction.To = Address.Zero;
            Assert.True(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.False(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }

        [Test]
        public void When_to_empty_then_is_message_call()
        {
            Transaction transaction = new Transaction();
            transaction.To = null;
            Assert.False(transaction.IsMessageCall, nameof(Transaction.IsMessageCall));
            Assert.True(transaction.IsContractCreation, nameof(Transaction.IsContractCreation));
        }
        
        [TestCase(1, true)]
        [TestCase(300, true)]
        public void IsEip1559_returns_expected_results(int decodedFeeCap, bool expectedIsEip1559)
        {
            Transaction transaction = new Transaction();
            transaction.DecodedMaxFeePerGas = (uint)decodedFeeCap;
            transaction.Type = TxType.EIP1559;
            Assert.AreEqual(transaction.MaxFeePerGas, transaction.DecodedMaxFeePerGas);
            Assert.AreEqual(expectedIsEip1559, transaction.IsEip1559);
        }
    }
}
