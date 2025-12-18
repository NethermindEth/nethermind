// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test;

internal class BlockTests
{
    [TestCaseSource(nameof(WithdrawalsTestCases))]
    public void Should_init_withdrawals_in_body_as_expected((BlockHeader Header, int? Count) fixture) =>
        (new Block(fixture.Header).Body.Withdrawals?.Length).Should().Be(fixture.Count);

    private static IEnumerable<(BlockHeader, int?)> WithdrawalsTestCases() =>
        new[]
        {
            (new BlockHeader(), (int?)null),
            (new BlockHeader { WithdrawalsRoot = Keccak.EmptyTreeHash }, 0)
        };

    [Test]
    public void DisposeAccountChanges_should_dispose_and_null_account_changes()
    {
        Block block = new(new BlockHeader());
        block.AccountChanges = new ArrayPoolList<AddressAsKey>(10);
        block.AccountChanges.Add(TestItem.AddressA);

        block.DisposeAccountChanges();

        block.AccountChanges.Should().BeNull();
    }
}
