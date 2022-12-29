// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

internal class BlockTests
{
    [Test]
    public void Should_init_withdrawals_in_body_as_expected()
    {
        var header = new BlockHeader();
        var block = new Block(header);

        block.Body.Withdrawals.Should().BeNull();

        header.WithdrawalsRoot = Keccak.EmptyTreeHash;

        block = new(header);

        block.Body.Withdrawals.Should().BeEmpty();
    }
}
