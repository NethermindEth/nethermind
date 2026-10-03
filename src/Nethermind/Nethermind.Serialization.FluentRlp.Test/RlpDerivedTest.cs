// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        Player player = new(Id: 42, Username: "SuperUser");
        byte[] rlp = Rlp.Write(player, static (ref RlpWriter w, Player player) => w.Write(player));

        Player decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayer());
        Assert.That(decoded, Is.EqualTo(player));
    }

    [Test]
    public void RecordWithList()
    {
        PlayerWithFriends player = new(Id: 42, Username: "SuperUser", Friends: ["ana", "bob"]);
        byte[] rlp = Rlp.Write(player, static (ref RlpWriter w, PlayerWithFriends player) => w.Write(player));

        PlayerWithFriends decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithFriends());
        Assert.That(decoded, Is.EqualTo(player).UsingPropertiesComparer());
    }

    [Test]
    public void RecordWithArray()
    {
        PlayerWithCodes player = new(Id: 42, Username: "SuperUser", Codes: [2, 4, 8, 16, 32, 64]);
        byte[] rlp = Rlp.Write(player, static (ref RlpWriter w, PlayerWithCodes player) => w.Write(player));

        PlayerWithCodes decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithCodes());
        Assert.That(decoded, Is.EqualTo(player).UsingPropertiesComparer());
    }

    [Test]
    public void RecordWithDictionary()
    {
        PlayerWithScores player = new(Id: 42, Username: "SuperUser", Scores: new()
        {
            { "foo", 42 },
            { "bar", 1337 }
        });
        byte[] rlp = Rlp.Write(player, static (ref RlpWriter w, PlayerWithScores player) => w.Write(player));

        PlayerWithScores decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithScores());
        Assert.That(decoded, Is.EqualTo(player).UsingPropertiesComparer());
    }

    [Test]
    public void RecordWithTuple()
    {
        IntegerTuple integerTuple = new((42, 1337));
        byte[] rlp = Rlp.Write(integerTuple, static (ref RlpWriter w, IntegerTuple tuple) => w.Write(tuple));

        IntegerTuple decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadIntegerTuple());
        Assert.That(decoded, Is.EqualTo(integerTuple));
    }

    [Test]
    public void RecursiveRecord()
    {
        Tree tree = new("foo",
        [
            new Tree("bar",
                [new Tree("dog", [])]),
            new Tree("qux",
                [new Tree("cat", [])])
        ]);
        byte[] rlp = Rlp.Write(tree, static (ref RlpWriter w, Tree tree) => w.Write(tree));

        Tree decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadTree());
        Assert.That(decoded, Is.EqualTo(tree).UsingPropertiesComparer());
    }

    [Test]
    public void NewtypeRecords()
    {
        Address address = new("0x1234567890ABCDEF");

        byte[] rlp = Rlp.Write(address, static (ref RlpWriter writer, Address address)
            => writer.Write(address));

        byte[] rlpExplicit = Rlp.Write(address, (ref RlpWriter writer, Address value)
            => writer.Write(value.HexString));

        Assert.That(rlp, Is.EqualTo(rlpExplicit));

        Address decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadAddress());

        Assert.That(decoded, Is.EqualTo(address));
    }

    [Test]
    public void RecordWithNestedGenerics()
    {
        AccessList accessList = new([
            (new Address("0x1234567890ABCDEF"), [1, 1, 3, 5, 8, 13]),
            (new Address("0xFEDCBA0987654321"), [2, 4, 6, 8, 10])
        ]);

        byte[] rlp = Rlp.Write(accessList, (ref RlpWriter writer, AccessList value) => writer.Write(value));

        AccessList decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadAccessList());
        Assert.That(decoded, Is.EqualTo(accessList).UsingPropertiesComparer());
    }

    [Test]
    public void RecordWithFixedLength()
    {
        FixedAddress fixedAddress = new([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);

        byte[] rlp = Rlp.Write(fixedAddress, (ref RlpWriter writer, FixedAddress value) => writer.Write(value));

        FixedAddress decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadFixedAddress());
        Assert.That(decoded, Is.EqualTo(fixedAddress).UsingPropertiesComparer());
    }
}
