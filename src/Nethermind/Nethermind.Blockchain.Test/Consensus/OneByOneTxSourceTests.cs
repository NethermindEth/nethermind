// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class OneByOneTxSourceTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_serve_one_by_one()
        {
            ITxSource source = Substitute.For<ITxSource>();
            BlockHeader blockHeader = new();
            source.GetTransactions(blockHeader, 0).Returns(new Transaction[5]);

            ITxSource oneByOne = source.ServeTxsOneByOne();
            oneByOne.GetTransactions(blockHeader, 0).Count().Should().Be(1);

        }
    }
}
