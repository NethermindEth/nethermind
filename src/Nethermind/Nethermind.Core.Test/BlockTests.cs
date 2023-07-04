// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
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
}
