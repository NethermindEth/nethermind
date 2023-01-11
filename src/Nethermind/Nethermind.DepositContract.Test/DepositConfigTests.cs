// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.DepositContract.Test
{
    [TestFixture]
    public class DepositConfigTests
    {
        [Test]
        public void defaults_are_fine()
        {
            string address = TestItem.AddressA.ToString();
            DepositConfig depositConfig = new DepositConfig();
            depositConfig.DepositContractAddress.Should().BeNull();
            depositConfig.DepositContractAddress = address;
            depositConfig.DepositContractAddress.Should().Be(address);
        }
    }
}
