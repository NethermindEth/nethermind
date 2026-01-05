// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Serialization.FluentRlp.Generator;
using Nethermind.Serialization.FluentRlp.Instances;
using NUnit.Framework;

namespace Nethermind.Serialization.FluentRlp.Test;

[RlpSerializable]
public record Player(int Id, string Username);

[RlpSerializable]
public record PlayerWithFriends(int Id, string Username, List<string> Friends);

[RlpSerializable]
public record PlayerWithScores(int Id, string Username, Dictionary<string, int> Scores);

[RlpSerializable]
public record PlayerWithCodes(int Id, string Username, int[] Codes);

[RlpSerializable]
public record Tree(string Value, List<Tree> Children);

[RlpSerializable]
public record RawData(int Tag, byte[] Data);

[RlpSerializable]
public record Integers(short A, int B, long C, Int128 D);

[RlpSerializable]
public record IntegerTuple((int, long) Values);

[RlpSerializable(RlpRepresentation.Newtype)]
public record Address(string HexString);

[RlpSerializable]
public record AccessList(List<(Address, List<long>)> Entries);

[RlpSerializable(representation: RlpRepresentation.Newtype, length: Size)]
public record FixedAddress(byte[] Bytes)
{
    public const int Size = 20;
}

public class RlpDerivedTest
{
    [Test]
    public void FlatRecord()
    {
        var player = new Player(Id: 42, Username: "SuperUser");
        var rlp = Rlp.Write(player, static (ref RlpWriter w, Player player) => w.Write(player));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayer());
        decoded.Should().BeEquivalentTo(player);
    }

    [Test]
    public void RecordWithList()
    {
        var player = new PlayerWithFriends(Id: 42, Username: "SuperUser", Friends: ["ana", "bob"]);
        var rlp = Rlp.Write(player, static (ref RlpWriter w, PlayerWithFriends player) => w.Write(player));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithFriends());
        decoded.Should().BeEquivalentTo(player);
    }

    [Test]
    public void RecordWithArray()
    {
        var player = new PlayerWithCodes(Id: 42, Username: "SuperUser", Codes: [2, 4, 8, 16, 32, 64]);
        var rlp = Rlp.Write(player, static (ref RlpWriter w, PlayerWithCodes player) => w.Write(player));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithCodes());
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
        var rlp = Rlp.Write(player, static (ref RlpWriter w, PlayerWithScores player) => w.Write(player));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithScores());
        decoded.Should().BeEquivalentTo(player);
    }

    [Test]
    public void RecordWithTuple()
    {
        var integerTuple = new IntegerTuple((42, 1337));
        var rlp = Rlp.Write(integerTuple, static (ref RlpWriter w, IntegerTuple tuple) => w.Write(tuple));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadIntegerTuple());
        decoded.Should().BeEquivalentTo(integerTuple);
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
        var rlp = Rlp.Write(tree, static (ref RlpWriter w, Tree tree) => w.Write(tree));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadTree());
        decoded.Should().BeEquivalentTo(tree);
    }

    [Test]
    public void NewtypeRecords()
    {
        var address = new Address("0x1234567890ABCDEF");

        var rlp = Rlp.Write(address, static (ref RlpWriter writer, Address address)
            => writer.Write(address));

        var rlpExplicit = Rlp.Write(address, (ref RlpWriter writer, Address value)
            => writer.Write(value.HexString));

        rlp.Should().BeEquivalentTo(rlpExplicit);

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadAddress());

        decoded.Should().BeEquivalentTo(address);
    }

    [Test]
    public void RecordWithNestedGenerics()
    {
        var accessList = new AccessList([
            (new Address("0x1234567890ABCDEF"), [1, 1, 3, 5, 8, 13]),
            (new Address("0xFEDCBA0987654321"), [2, 4, 6, 8, 10])
        ]);

        var rlp = Rlp.Write(accessList, (ref RlpWriter writer, AccessList value) => writer.Write(value));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadAccessList());
        decoded.Should().BeEquivalentTo(accessList);
    }

    [Test]
    public void RecordWithFixedLength()
    {
        var fixedAddress = new FixedAddress([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);

        var rlp = Rlp.Write(fixedAddress, (ref RlpWriter writer, FixedAddress value) => writer.Write(value));

        var decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadFixedAddress());
        decoded.Should().BeEquivalentTo(fixedAddress);
    }
}
