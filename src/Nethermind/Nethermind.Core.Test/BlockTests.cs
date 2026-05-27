// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test;

internal class BlockTests
{
    [TestCaseSource(nameof(WithdrawalsTestCases))]
    public void Should_init_withdrawals_in_body_as_expected((BlockHeader Header, int? Count) fixture) =>
        Assert.That((new Block(fixture.Header).Body.Withdrawals?.Length), Is.EqualTo(fixture.Count));

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
        block.AccountChanges = new ArrayPoolList<AddressAsKey>(10)
        {
            TestItem.AddressA
        };

        block.DisposeAccountChanges();

        Assert.That(block.AccountChanges, Is.Null);
    }

    [Test]
    public void Equals_and_hash_code_ignore_processing_and_cache_fields()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Block block = new(header, [], []);
        Block sameCanonicalBlock = new(header.Clone(), [], []);

        block.BlockAccessList = new ReadOnlyBlockAccessList();
        block.GeneratedBlockAccessList = new GeneratedBlockAccessList();
        block.ExecutionRequests = [[1]];
        block.AccountChanges = new ArrayPoolList<AddressAsKey>(1)
        {
            TestItem.AddressA
        };
        block.EncodedBlockAccessList = [2];
        block.EncodedTransactions = [[3]];

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(block, Is.EqualTo(sameCanonicalBlock));
                Assert.That(block.GetHashCode(), Is.EqualTo(sameCanonicalBlock.GetHashCode()));
            });
        }
        finally
        {
            block.AccountChanges.Dispose();
        }
    }
}
