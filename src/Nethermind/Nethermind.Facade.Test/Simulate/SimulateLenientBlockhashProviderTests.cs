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
public class SimulateLenientBlockhashProviderTests
{
    [Test]
    public void Returns_null_instead_of_throwing_when_in_window_hash_cannot_be_resolved()
    {
        IBlockhashCache cache = Substitute.For<IBlockhashCache>();
        cache.GetHash(Arg.Any<BlockHeader>(), Arg.Any<ulong>()).Returns((Hash256?)null);
        IWorldState worldState = Substitute.For<IWorldState>();
        SimulateLenientBlockhashProvider sut = new(cache, worldState, LimboLogs.Instance);
        BlockHeader current = Build.A.BlockHeader.WithNumber(300).WithParentHash(TestItem.KeccakA).TestObject;

        // Same scenario that throws for canonical BlockhashProvider must yield 0 (null) under simulate.
        Assert.That(sut.GetBlockhash(current, 100, Frontier.Instance), Is.Null);
    }
}
