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

using FluentAssertions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mining.Test
{
    [TestFixture]
    public class MinGasPriceTests
    {
        [TestCase(0L, 0L, true)]
        [TestCase(1L, 0L, false)]
        [TestCase(1L, 1L, true)]
        [TestCase(1L, 2L, true)]
        [TestCase(2L, 1L, false)]
        public void Test(long minimum, long actual, bool expectedResult)
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(new ReleaseSpec()
            {
                IsEip1559Enabled = false
            });
            MinGasPriceTxFilter _filter = new MinGasPriceTxFilter((UInt256)minimum, specProvider);
            Transaction tx = Build.A.Transaction.WithGasPrice((UInt256)actual).TestObject;
            _filter.IsAllowed(tx, null).Allowed.Should().Be(expectedResult);
        }
        
        [TestCase(0L, 0L, 0L, true)]
        [TestCase(1L, 0L, 0L,false)]
        [TestCase(1L, 0L, 1L, false)]
        [TestCase(1L, 100L, 1000L, false)]
        [TestCase(1L, 875L, 1000L, false)]
        [TestCase(1L, 876L, 1000L, true)]
        [TestCase(1L, 876L, 0L, false)]
        [TestCase(2L, 1000L, 1L,false)]
        [TestCase(2L, 1000L, 1000L, true)]
        public void Test1559(long minimum, long maxFeePerGas, long maxPriorityFeePerGas, bool expectedResult)
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>()).Returns(new ReleaseSpec()
            {
                IsEip1559Enabled = true
            });
            MinGasPriceTxFilter _filter = new MinGasPriceTxFilter((UInt256)minimum, specProvider);
            Transaction tx = Build.A.Transaction.WithGasPrice(0)
                .WithMaxFeePerGas((UInt256)maxFeePerGas)
                .WithMaxPriorityFeePerGas((UInt256)maxPriorityFeePerGas)
                .WithType(TxType.EIP1559).TestObject;
            BlockBuilder blockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithGasLimit(10000).WithBaseFeePerGas((UInt256)1000);
            _filter.IsAllowed(tx, blockBuilder.TestObject.Header).Allowed.Should().Be(expectedResult);
        }
    }
}
