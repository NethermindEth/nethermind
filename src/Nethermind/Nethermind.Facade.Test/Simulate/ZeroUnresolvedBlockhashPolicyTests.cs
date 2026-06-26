// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Facade.Simulate;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

[TestFixture]
public class ZeroUnresolvedBlockhashPolicyTests
{
    [Test]
    public void BlockhashProvider_returns_null_instead_of_throwing_under_zero_policy()
    {
        IBlockhashCache cache = Substitute.For<IBlockhashCache>();
        cache.GetHash(Arg.Any<BlockHeader>(), Arg.Any<ulong>()).Returns((Hash256?)null);
        BlockhashProvider sut = new(cache, Substitute.For<IWorldState>(), LimboLogs.Instance, new ZeroUnresolvedBlockhashPolicy());
        BlockHeader current = Build.A.BlockHeader.WithNumber(300).WithParentHash(TestItem.KeccakA).TestObject;

        // Same in-window cache miss that throws for canonical BlockhashProvider must yield 0 (null) under simulate.
        Assert.That(sut.GetBlockhash(current, 100, Frontier.Instance), Is.Null);
    }
}
