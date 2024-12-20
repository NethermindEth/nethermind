// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Serialization.FastRlp.Derived;

namespace Nethermind.Serialization.FastRlp.Test;

public class RlpDerivedTest
{
    [Test]
    public void ReadDerivedRecord()
    {
        var player = new Player(42, "SuperUser");
        ReadOnlySpan<byte> rlp = Rlp.Write((ref RlpWriter w) => w.Write(player));

        var decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.ReadPlayer());
        decoded.Should().Be(player);
    }
}
