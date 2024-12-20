// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
// Automatically imports all source-generated instances
using Nethermind.Serialization.FastRlp.Derived;
// Imports `RlpSerializable` attribute
using Nethermind.Serialization.FastRlp.Generator;

namespace Nethermind.Serialization.FastRlp.Test;

[RlpSerializable]
public record Player(int Id, string Username);

public class RlpDerivedTest
{
    [Test]
    public void ReadDerivedRecord()
    {
        var player = new Player(Id: 42, Username: "SuperUser");
        ReadOnlySpan<byte> rlp = Rlp.Write((ref RlpWriter w) => w.Write(player));

        var decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.ReadPlayer());
        decoded.Should().Be(player);
    }
}
