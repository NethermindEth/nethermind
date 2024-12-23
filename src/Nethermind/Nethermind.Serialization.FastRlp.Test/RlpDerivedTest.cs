// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Serialization.FastRlp.Generator;

namespace Nethermind.Serialization.FastRlp.Test;

[RlpSerializable]
public record Player(int Id, string Username);

[RlpSerializable]
public record PlayerWithFriends(int Id, string Username, List<string> Friends);

public class RlpDerivedTest
{
    [Test]
    public void FlatRecord()
    {
        var player = new Player(Id: 42, Username: "SuperUser");
        ReadOnlySpan<byte> rlp = Rlp.Write((ref RlpWriter w) => w.Write(player));

        var decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.ReadPlayer());
        decoded.Should().BeEquivalentTo(player);
    }

    [Test]
    public void RecordWithList()
    {
        var player = new PlayerWithFriends(Id: 42, Username: "SuperUser", Friends: ["ana", "bob"]);
        ReadOnlySpan<byte> rlp = Rlp.Write((ref RlpWriter w) => w.Write(player));

        var decoded = Rlp.Read(rlp, (scoped ref RlpReader r) => r.ReadPlayerWithFriends());
        decoded.Should().BeEquivalentTo(player);
    }
}
