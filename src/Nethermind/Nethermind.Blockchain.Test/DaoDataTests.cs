// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class DaoDataTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Test()
        {
            DaoData.DaoAccounts.Should().HaveCount(116);
            DaoData.DaoWithdrawalAccount.Should().Be(
                new Address("bf4ed7b27f1d666546e30d74d50d173d20bca754"));
        }
    }
}
