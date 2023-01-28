// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.DepositContract.Test.JsonRpc
{
    [TestFixture]
    public class DepositContractTests
    {
        [Test]
        public void event_hash_is_valid()
        {
            DepositContract depositContract = new DepositContract(new AbiEncoder(), Address.Zero);
            depositContract.DepositEventHash.Should().Be(new Keccak("0x649bbc62d0e31342afea4e5cd82d4049e7e1ee912fc0889aa790803be39038c5"));
        }

        [Test]
        public void can_make_deposit()
        {
            DepositContract depositContract = new DepositContract(new AbiEncoder(), Address.Zero);
            Transaction transaction = depositContract.Deposit(TestItem.AddressA, new byte[42], new byte[32], new byte[96], new byte[32]);
            transaction.Data.Should().HaveCount(484);
        }
    }
}
