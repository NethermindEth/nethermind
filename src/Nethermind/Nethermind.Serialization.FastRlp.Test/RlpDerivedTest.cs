// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Serialization.FastRlp.Generator;

namespace Nethermind.Serialization.FastRlp.Test;

[RlpSerializable]
public record Player(int Id, string Username);

[RlpSerializable]
public record PlayerWithFriends(int Id, string Username, List<string> Friends);

[RlpSerializable]
public record PlayerWithScores(int Id, string Username, Dictionary<string, int> Scores);

[RlpSerializable]
public record Tree(string Value, List<Tree> Children);

[RlpSerializable]
public record RawData(int Tag, byte[] Data);

[RlpSerializable]
public record Integers(short A, int B, long C, Int128 D);

public class RlpDerivedTest
{
    [Test]
    public void FlatRecord()
    {
        var player = new Player(Id: 42, Username: "SuperUser");
        ReadOnlySpan<byte> rlp = Rlp.Write(player, static (ref RlpWriter w, Player player) => w.Write(player));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayer());
        decoded.Should().BeEquivalentTo(player);
    }

    [Test]
    public void RecordWithList()
    {
        var player = new PlayerWithFriends(Id: 42, Username: "SuperUser", Friends: ["ana", "bob"]);
        ReadOnlySpan<byte> rlp = Rlp.Write(player, static (ref RlpWriter w, PlayerWithFriends player) => w.Write(player));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithFriends());
        decoded.Should().BeEquivalentTo(player);
    }

    [Test]
    public void RecordWithDictionary()
    {
        var player = new PlayerWithScores(Id: 42, Username: "SuperUser", Scores: new()
        {
            { "foo", 42 },
            { "bar", 1337 }
        });
        ReadOnlySpan<byte> rlp = Rlp.Write(player, static (ref RlpWriter w, PlayerWithScores player) => w.Write(player));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithScores());
        decoded.Should().BeEquivalentTo(player);
    }

    [Test]
    public void RecursiveRecord()
    {
        var tree = new Tree("foo",
        [
            new Tree("bar",
                [new Tree("dog", [])]),
            new Tree("qux",
                [new Tree("cat", [])])
        ]);
        ReadOnlySpan<byte> rlp = Rlp.Write(tree, static (ref RlpWriter w, Tree tree) => w.Write(tree));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadTree());
        decoded.Should().BeEquivalentTo(tree);
    }
}
