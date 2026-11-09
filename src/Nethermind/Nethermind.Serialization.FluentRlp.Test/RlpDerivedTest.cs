// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nethermind.Int256;
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
public record Codes(int Id, int[] Values);

[RlpSerializable]
public record PlayerWithCodes(int Id, string Username, int[] Codes);

[RlpSerializable]
public record Tree(string Value, List<Tree> Children);

[RlpSerializable]
public record RawData(int Tag, byte[] Data);

[RlpSerializable]
public record DataBlocks(RawData[] Values);

[RlpSerializable]
public record Integers(short A, int B, long C, Int128 D);

[RlpSerializable]
public record UnsignedIntegers(byte A, ushort B, uint C, ulong D, UInt128 E, UInt256 F);

[RlpSerializable]
public record IntegerTuple((int, long) Values);

[RlpSerializable]
public record IntegerTuples((int, long)[] Values);

[RlpSerializable(RlpRepresentation.Newtype, length: 32)]
public record FixedKey(byte[] Bytes);

[RlpSerializable]
public record FixedEntry([RlpFixedLength(20)] byte[] Address, List<FixedKey> Keys);

[RlpSerializable]
public record NamedEntry([RlpFixedLength(20)] byte[] Address, string Name);

[RlpSerializable]
public record StorageSlot([RlpFixedLength(32)] UInt256 Key, UInt256 Value);

[RlpFixedLength(32)]
public record Blob(byte[] Bytes);

// A hand-written converter that does not keep the declared length, to test the checks of the generated code.
public abstract class BlobRlpConverter : IRlpConverter<Blob>
{
    public static Blob Read(ref RlpReader reader) => new(reader.ReadBytes().ToArray());
    public static void Write(ref RlpWriter writer, Blob value) => writer.Write(value.Bytes);
}

public static class BlobRlpConverterExt
{
    public static Blob ReadBlob(this ref RlpReader reader) => BlobRlpConverter.Read(ref reader);
    public static void Write(this ref RlpWriter writer, Blob value) => BlobRlpConverter.Write(ref writer, value);
}

[RlpSerializable]
public record Blobs(List<Blob> Items);

[RlpSerializable]
public record BlobPair((Blob, Blob) Values);

[RlpSerializable]
public record BlobMap(Dictionary<Blob, Blob> Values);

[RlpSerializable]
public record BlobPairList(List<(Blob, Blob)> Values);

[RlpSerializable]
public record UnhintedEntry(byte[] Address, List<byte[]> Keys);

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
        byte[] rlp = RlpCodec.Write(player, static (ref RlpWriter w, Player player) => w.Write(player));

        Player decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayer());
        Assert.That(decoded, Is.EqualTo(player));
    }

    [Test]
    public void RecordWithList()
    {
        PlayerWithFriends player = new(Id: 42, Username: "SuperUser", Friends: ["ana", "bob"]);
        byte[] rlp = RlpCodec.Write(player, static (ref RlpWriter w, PlayerWithFriends player) => w.Write(player));

        PlayerWithFriends decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithFriends());
        Assert.That(decoded, Is.EqualTo(player).UsingPropertiesComparer());
    }

    [Test]
    public void RecordWithArray()
    {
        PlayerWithCodes player = new(Id: 42, Username: "SuperUser", Codes: [2, 4, 8, 16, 32, 64]);
        byte[] rlp = RlpCodec.Write(player, static (ref RlpWriter w, PlayerWithCodes player) => w.Write(player));

        PlayerWithCodes decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithCodes());
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
        byte[] rlp = RlpCodec.Write(player, static (ref RlpWriter w, PlayerWithScores player) => w.Write(player));

        PlayerWithScores decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadPlayerWithScores());
        Assert.That(decoded, Is.EqualTo(player).UsingPropertiesComparer());
    }

    [Test]
    public void RecordWithTuple()
    {
        IntegerTuple integerTuple = new((42, 1337));
        byte[] rlp = RlpCodec.Write(integerTuple, static (ref RlpWriter w, IntegerTuple tuple) => w.Write(tuple));

        IntegerTuple decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadIntegerTuple());
        Assert.That(decoded, Is.EqualTo(integerTuple));
    }

    [Test]
    public void RecordWithTupleArray()
    {
        IntegerTuples tuples = new([(1, 2), (3, 4), (5, 6)]);
        byte[] rlp = RlpCodec.Write(tuples, static (ref RlpWriter w, IntegerTuples tuples) => w.Write(tuples));

        IntegerTuples decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadIntegerTuples());
        Assert.That(decoded.Values, Is.EqualTo(tuples.Values));
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
        byte[] rlp = RlpCodec.Write(tree, static (ref RlpWriter w, Tree tree) => w.Write(tree));

        Tree decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadTree());
        Assert.That(decoded, Is.EqualTo(tree).UsingPropertiesComparer());
    }

    // A record that contains itself must not compute subtree lengths at every level, so it writes through the buffered path.
    [Test]
    public void DeepRecursiveRecord()
    {
        Tree tree = new("leaf", []);
        for (int i = 0; i < 1_000; i++) tree = new Tree("node", [tree]);

        byte[] rlp = RlpCodec.Write(tree, static (ref RlpWriter w, Tree tree) => w.Write(tree));
        Tree decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadTree());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(typeof(TreeRlpConverter).IsAssignableTo(typeof(IRlpLengthConverter<Tree>)), Is.False);
            Assert.That(RlpCodec.Write(decoded, static (ref RlpWriter w, Tree tree) => w.Write(tree)), Is.EqualTo(rlp));
        }
    }

    [Test]
    public void NewtypeRecords()
    {
        Address address = new("0x1234567890ABCDEF");

        byte[] rlp = RlpCodec.Write(address, static (ref RlpWriter writer, Address address)
            => writer.Write(address));

        byte[] rlpExplicit = RlpCodec.Write(address, (ref RlpWriter writer, Address value)
            => writer.Write(value.HexString));

        Assert.That(rlp, Is.EqualTo(rlpExplicit));

        Address decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadAddress());

        Assert.That(decoded, Is.EqualTo(address));
    }

    [Test]
    public void RecordWithNestedGenerics()
    {
        AccessList accessList = new([
            (new Address("0x1234567890ABCDEF"), [1, 1, 3, 5, 8, 13]),
            (new Address("0xFEDCBA0987654321"), [2, 4, 6, 8, 10])
        ]);

        byte[] rlp = RlpCodec.Write(accessList, (ref RlpWriter writer, AccessList value) => writer.Write(value));

        AccessList decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadAccessList());
        Assert.That(decoded, Is.EqualTo(accessList).UsingPropertiesComparer());
    }

    [Test]
    public void RecordWithFixedLength()
    {
        FixedAddress fixedAddress = new([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);

        byte[] rlp = RlpCodec.Write(fixedAddress, (ref RlpWriter writer, FixedAddress value) => writer.Write(value));

        FixedAddress decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadFixedAddress());
        Assert.That(decoded, Is.EqualTo(fixedAddress).UsingPropertiesComparer());
    }

    [Test]
    public void RecordWithUnsignedIntegerLimits()
    {
        UnsignedIntegers integers = new(byte.MaxValue, ushort.MaxValue, uint.MaxValue, ulong.MaxValue, UInt128.MaxValue, UInt256.MaxValue);
        byte[] rlp = RlpCodec.Write(integers, static (ref RlpWriter w, UnsignedIntegers value) => w.Write(value));

        UnsignedIntegers decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadUnsignedIntegers());
        Assert.That(decoded, Is.EqualTo(integers));
    }

    [Test]
    public void RecordWithFixedLengthRejectsOtherLengths()
    {
        byte[] shortRlp = RlpCodec.Write(static (ref RlpWriter w) => w.Write(new byte[FixedAddress.Size - 1]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => RlpCodec.Read(shortRlp, static (scoped ref RlpReader r) => r.ReadFixedAddress()), Throws.TypeOf<RlpReaderException>());
            Assert.That(() => RlpCodec.Write(new FixedAddress(new byte[FixedAddress.Size + 1]), static (ref RlpWriter w, FixedAddress value) => w.Write(value)),
                Throws.ArgumentException);
        }
    }

    [Test]
    public void RecordWithFixedLengthHints([Values(0, 1, 3)] int keyCount)
    {
        byte[] address = Enumerable.Range(1, 20).Select(static b => (byte)b).ToArray();
        List<byte[]> keys = Enumerable.Range(0, keyCount).Select(static i => Enumerable.Repeat((byte)(i + 1), 32).ToArray()).ToList();
        FixedEntry entry = new(address, keys.Select(static k => new FixedKey(k)).ToList());

        byte[] rlp = RlpCodec.Write(entry, static (ref RlpWriter w, FixedEntry value) => w.Write(value));
        byte[] unhinted = RlpCodec.Write(new UnhintedEntry(address, keys), static (ref RlpWriter w, UnhintedEntry value) => w.Write(value));
        FixedEntry decoded = RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadFixedEntry());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rlp, Is.EqualTo(unhinted));
            Assert.That(decoded.Address, Is.EqualTo(address));
            Assert.That(decoded.Keys.Select(static k => k.Bytes), Is.EqualTo(keys));
            Assert.That(() => RlpCodec.Write(entry with { Address = new byte[19] }, static (ref RlpWriter w, FixedEntry value) => w.Write(value)),
                Throws.InvalidOperationException);
        }
    }

    // A converter that knows its length writes the same bytes in one pass to an exact array.
    [Test]
    public void LengthConvertersWriteTheSameBytes()
    {
        using (Assert.EnterMultipleScope())
        {
            AssertDirect<FixedEntryRlpConverter, FixedEntry>(new FixedEntry(new byte[20], [new FixedKey(new byte[32]), new FixedKey(Enumerable.Repeat((byte)7, 32).ToArray())]));
            AssertDirect<FixedEntryRlpConverter, FixedEntry>(new FixedEntry(new byte[20], []));
            AssertDirect<IntegersRlpConverter, Integers>(new Integers(0, int.MaxValue, 0x80, Int128.MaxValue));
            AssertDirect<UnsignedIntegersRlpConverter, UnsignedIntegers>(new UnsignedIntegers(0, 0x7f, 0x80, ulong.MaxValue, UInt128.MaxValue, UInt256.MaxValue));
            AssertDirect<CodesRlpConverter, Codes>(new Codes(1, Enumerable.Range(0, 100).ToArray()));
            AssertDirect<RawDataRlpConverter, RawData>(new RawData(5, new byte[300]));
            AssertDirect<RawDataRlpConverter, RawData>(new RawData(5, [0x7f]));
        }
    }

    [Test]
    public void LengthConverterWithWrongFixedLength()
        => Assert.That(static () => RlpCodec.Write<FixedEntryRlpConverter, FixedEntry>(new FixedEntry(new byte[19], [])), Throws.InvalidOperationException);

    [Test]
    public void GeneratedLengthRejectsOverflow()
    {
        RawData block = new(0, new byte[1 << 20]);
        DataBlocks blocks = new(Enumerable.Repeat(block, 2048).ToArray());
        Assert.That(() => DataBlocksRlpConverter.GetLength(blocks), Throws.TypeOf<OverflowException>());
    }

    [Test]
    public void EmptyArraysUseSharedInstance([Values] bool generated)
    {
        int[] values = generated
            ? Rlp.Read(new byte[] { 0xc2, 0x80, 0xc0 }, static (scoped ref RlpReader r) => r.ReadCodes()).Values
            : Rlp.Read(new byte[] { 0xc0 }, static (scoped ref RlpReader r) => r.ReadArray(static (scoped ref RlpReader r) => r.ReadInt32()));

        Assert.That(values, Is.SameAs(Array.Empty<int>()));
    }

    // The integer converter would strip the leading zeros of a small key. Different limbs check their order.
    [Test]
    public void FixedLengthUInt256([Values] bool differentLimbs)
    {
        StorageSlot slot = new(differentLimbs ? new UInt256(1, 2, 3, 4) : UInt256.One, UInt256.One);
        byte[] rlp = RlpCodec.Write<StorageSlotRlpConverter, StorageSlot>(slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rlp, Has.Length.EqualTo(1 + 33 + 1));
            Assert.That(RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadStorageSlot()), Is.EqualTo(slot));
        }
    }

    [Test]
    public void ListOfFixedLengthItemsWithWrongLengths()
        => Assert.That(static () => RlpCodec.Write<BlobsRlpConverter, Blobs>(new Blobs([new Blob(new byte[31]), new Blob(new byte[33])])), Throws.InvalidOperationException);

    [Test]
    public void NestedFixedLengthValues([Values(31, 32, 33)] int firstLength, [Values(31, 32, 33)] int secondLength, [Values] bool dictionary, [Values] bool read)
    {
        Blob first = new(new byte[firstLength]);
        Blob second = new(new byte[secondLength]);
        byte[] encoded = [];
        if (read)
        {
            encoded = Rlp.Write(dictionary, (ref RlpWriter w, bool dictionary) => w.WriteSequence(dictionary, (ref RlpWriter w, bool dictionary) =>
            {
                if (dictionary)
                {
                    w.WriteSequence((first, second), static (ref RlpWriter w, (Blob First, Blob Second) pair) =>
                        w.WriteSequence(pair, static (ref RlpWriter w, (Blob First, Blob Second) pair) =>
                        {
                            w.Write(pair.First.Bytes);
                            w.Write(pair.Second.Bytes);
                        }));
                }
                else
                {
                    w.Write(first.Bytes);
                    w.Write(second.Bytes);
                }
            }));
        }

        void Convert()
        {
            if (read)
            {
                if (dictionary) _ = Rlp.Read(encoded, static (scoped ref RlpReader r) => r.ReadBlobMap());
                else _ = Rlp.Read(encoded, static (scoped ref RlpReader r) => r.ReadBlobPair());
            }
            else
            {
                if (dictionary) _ = Rlp.Write(new BlobMap(new() { [first] = second }), static (ref RlpWriter w, BlobMap value) => w.Write(value));
                else _ = Rlp.Write(new BlobPair((first, second)), static (ref RlpWriter w, BlobPair value) => w.Write(value));
            }
        }

        if (firstLength == 32 && secondLength == 32) Assert.That(Convert, Throws.Nothing);
        else if (read) Assert.That(Convert, Throws.TypeOf<RlpReaderException>());
        else Assert.That(Convert, Throws.InvalidOperationException);
    }

    [Test]
    public void ListOfFixedLengthPairs([Values(31, 32, 33)] int firstLength)
    {
        BlobPairList value = new([(new Blob(new byte[firstLength]), new Blob(new byte[64 - firstLength]))]);
        if (firstLength == 32)
        {
            AssertDirect<BlobPairListRlpConverter, BlobPairList>(value);
            byte[] encoded = Rlp.Write<BlobPairListRlpConverter, BlobPairList>(value);
            Assert.That(Rlp.Read(encoded, static (scoped ref RlpReader r) => r.ReadBlobPairList()), Is.EqualTo(value).UsingPropertiesComparer());
        }
        else
        {
            Assert.That(() => Rlp.Write<BlobPairListRlpConverter, BlobPairList>(value), Throws.InvalidOperationException);
        }
    }

    [Test]
    public void LengthConverterThatDoesNotEndASequence()
        => Assert.That(static () => RlpCodec.Write<OpenSequenceConverter, int>(0), Throws.InvalidOperationException);

    private abstract class OpenSequenceConverter : IRlpLengthConverter<int>
    {
        public static int Read(ref RlpReader reader) => throw new NotSupportedException();
        public static void Write(ref RlpWriter writer, int value) => writer.StartSequence(0);
        public static int GetLength(int value) => 1;
    }

    [Test]
    public void BufferedWriteWithWrongFixedLength()
        => Assert.That(static () => RlpCodec.Write(new NamedEntry(new byte[19], "name"), static (ref RlpWriter w, NamedEntry entry) => w.Write(entry)), Throws.InvalidOperationException);

    [Test]
    public void WrongFixedLengthDoesNotAllocatePayload()
    {
        byte[] encoded = Rlp.Write(static (ref RlpWriter w) => w.WriteSequence(static (ref RlpWriter w) =>
        {
            w.Write(new byte[1 << 20]);
            w.WriteSequence(static (ref RlpWriter _) => { });
        }));
        Reject(encoded);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Reject(encoded);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.LessThan(4096));

        static void Reject(byte[] encoded)
        {
            try
            {
                Rlp.Read(encoded, static (scoped ref RlpReader r) => r.ReadFixedEntry());
            }
            catch (RlpReaderException)
            {
                return;
            }

            Assert.Fail("Expected invalid fixed length");
        }
    }

    private static void AssertDirect<TConverter, T>(T value) where TConverter : IRlpLengthConverter<T>
    {
        byte[] expected = RlpCodec.Write(value, static (ref RlpWriter w, T value) => TConverter.Write(ref w, value));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RlpCodec.Write<TConverter, T>(value), Is.EqualTo(expected), typeof(T).Name);
            Assert.That(TConverter.GetLength(value), Is.EqualTo(expected.Length), typeof(T).Name);
        }
    }

    // RlpFixedLength must hold on read too, or a short field decodes and later fails to encode.
    [TestCase("c280c0", TestName = "Empty fixed-length field")]
    [TestCase("d593000102030405060708090a0b0c0d0e0f101112c0", TestName = "Fixed-length field one byte short")]
    [TestCase("f694000102030405060708090a0b0c0d0e0f10111213e09f11111111111111111111111111111111111111111111111111111111111111", TestName = "Fixed-length list item one byte short")]
    public void RecordWithFixedLengthHintsRejectsOtherLengths(string hex)
    {
        byte[] rlp = Convert.FromHexString(hex);

        Assert.That(() => RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadFixedEntry()), Throws.TypeOf<RlpReaderException>());
    }

    // A fixed-length UInt256 reads through ReadBytes(int), which checks prefix and length with one compare.
    [TestCase("e29f111111111111111111111111111111111111111111111111111111111111110102", TestName = "Fixed-length key one byte short")]
    [TestCase("e4a11111111111111111111111111111111111111111111111111111111111111111110102", TestName = "Fixed-length key one byte long")]
    public void StorageSlotRejectsOtherKeyLengths(string hex)
    {
        byte[] rlp = Convert.FromHexString(hex);

        Assert.That(() => RlpCodec.Read(rlp, static (scoped ref RlpReader r) => r.ReadStorageSlot()), Throws.TypeOf<RlpReaderException>());
    }

    [TestCase("public record Account(int Nonce) { public int Balance { get; init; } }", "RLP0002", TestName = "Settable property in the body")]
    [TestCase("public record Account(int Nonce) { public int Balance; }", "RLP0002", TestName = "Field in the body")]
    [TestCase("public record Account(int Nonce) { public int Balance { get; } public Account(int nonce, int balance) : this(nonce) => Balance = balance; }", "RLP0002", TestName = "Get-only property set in another constructor")]
    [TestCase("public record Account { public int Balance { get; init; } }", "RLP0003", TestName = "No primary constructor")]
    [TestCase("public record Wrapper<T>(T Value);", "RLP0003", TestName = "Generic record")]
    [TestCase("public record Outer(int A) { [RlpSerializable] public record Inner(int B); }", "RLP0003", TestName = "Nested record")]
    [TestCase("[RlpSerializable(RlpRepresentation.Newtype)] public record Pair(int A, int B);", "RLP0001", TestName = "Newtype with two parameters")]
    [TestCase("[RlpSerializable(length: 20)] public record Number(int A);", "RLP0005", TestName = "Length without a byte array")]
    [TestCase("public record Triple((int, int, int) A);", "RLP0004", TestName = "Tuple of three")]
    [TestCase("public record Account(int Nonce) { public int Twice => Nonce * 2; public int Cached { get; } = Nonce * 2; public const int Size = 1; public int Nonce { get; init; } = Nonce; }", null, TestName = "Derived and redeclared members")]
    [TestCase("public partial record Account(int Nonce); public partial record Account { public int Balance { get; init; } }", "RLP0002", TestName = "Settable property in another part")]
    [TestCase("public record Account(int Nonce) { public int Nonce { get; init; } = Nonce * 2; }", "RLP0002", TestName = "Redeclared parameter property with another value")]
    [TestCase("public record Base { public int Balance { get; init; } } [RlpSerializable] public record Account(int Nonce) : Base;", "RLP0003", TestName = "Derived record")]
    [TestCase("public record Flags(bool A);", "RLP0004", TestName = "Bool parameter")]
    [TestCase("public enum Kind { A } [RlpSerializable] public record Tagged(Kind A);", "RLP0004", TestName = "Enum parameter")]
    [TestCase("public record Keys(System.Collections.Generic.Dictionary<byte[], int> A);", "RLP0004", TestName = "Byte array dictionary key")]
    [TestCase("[RlpSerializable(RlpRepresentation.Newtype)] public record Loop(Loop Inner);", "RLP0006", TestName = "Recursive Newtype")]
    [TestCase("[RlpSerializable(RlpRepresentation.Newtype)] public record Loop((int, Loop) Inner);", "RLP0006", TestName = "Recursive Newtype through a tuple")]
    [TestCase("[RlpSerializable(RlpRepresentation.Newtype)] public record A(B Inner); [RlpSerializable(RlpRepresentation.Newtype)] public record B(A Inner);", "RLP0006", TestName = "Mutually recursive Newtypes")]
    [TestCase("public record Key([RlpFixedLength(1)] byte[] A);", "RLP0007", TestName = "Fixed length below 2")]
    [TestCase("public record Key([RlpFixedLength(int.MaxValue)] byte[] A);", "RLP0007", TestName = "Fixed length exceeds encoded length limit")]
    [TestCase("public record Key([RlpFixedLength(8)] long A);", "RLP0007", TestName = "Fixed length on an integer")]
    [TestCase("public record Key([RlpFixedLength(20)] string A);", "RLP0007", TestName = "Fixed length on a string")]
    [TestCase("public record Key([RlpFixedLength(20)] Nethermind.Int256.UInt256 A);", "RLP0007", TestName = "Fixed length of a UInt256 other than 32")]
    [TestCase("public record Key([RlpFixedLength(32)] Nethermind.Int256.UInt256 A);", null, TestName = "Fixed length of a UInt256 of 32")]
    [TestCase("public record Chunks(byte[][] A, int[][] B, (int, long)[] C, (byte[], int)[] D, System.Collections.Generic.List<int[]>[] E);", null, TestName = "Arrays of arrays and tuples")]
    [TestCase("internal record Hidden(int A);", null, TestName = "Internal record")]
    [TestCase("public record Names(int @class, string value, int r, int w, System.Collections.Generic.List<System.Collections.Generic.List<int>> items, int _0);", null, TestName = "Keyword and generated names as parameters")]
    [TestCase("public record Account(int Nonce) { public readonly int Balance = 0; public Account(int nonce, int balance) : this(nonce) => Balance = balance; }", "RLP0002", TestName = "Readonly initialized field reassigned by constructor")]
    [TestCase("public record Account(int Nonce) { public int Balance { get => field; init => field = value; } }", "RLP0002", TestName = "Field-backed settable property")]
    [TestCase("public partial record Account(int Nonce) { public readonly int Balance = 0; } public partial record Account { public Account(int nonce, int balance) : this(nonce) => Balance = balance; }", "RLP0002", TestName = "Readonly field assigned in another part")]
    [TestCase("public record Account(int Nonce) { public int Balance { get; } = 0; public Account(int nonce, int balance) : this(nonce) => Balance = balance; }", "RLP0002", TestName = "Initialized get-only property reassigned by constructor")]
    [TestCase("public record Account(int Nonce) { public readonly int Balance = Nonce * 2; public Account(string nonce) : this(int.Parse(nonce)) { } }", null, TestName = "Derived field with delegating constructor")]
    [TestCase("public record Account(int Nonce) { public int Balance => Nonce * 2; }", null, TestName = "Computed property without storage")]
    [TestCase("public record Account(int Nonce) { public readonly int Balance = 0; public Account(int nonce, int balance) : this(nonce) { (Balance, Nonce) = (balance, nonce); } }", "RLP0002", TestName = "Readonly field assigned by deconstruction")]
    public void GeneratorDiagnostics(string declaration, string? expectedId)
    {
        string source = $"""
            using Nethermind.Serialization.FluentRlp;
            namespace Diagnostics;
            {(declaration.StartsWith('[') ? "" : "[RlpSerializable] ")}{declaration}
            """;

        GeneratorDriverRunResult result = RunGenerator(source, out Compilation compilation);
        string[] ids = result.Diagnostics.Select(static d => d.Id).ToArray();

        if (expectedId is null)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ids, Is.Empty);
                Assert.That(compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
            }
        }
        else
        {
            Assert.That(ids, Does.Contain(expectedId));
        }
    }

    [Test]
    public void GeneratorPreservesImportScopes([Values(
        "namespace Probe { using Models; BODY }",
        "namespace Probe; using Models; BODY",
        "using Probe.Models; namespace Probe; BODY",
        "namespace Probe { using Probe.Models; BODY }",
        "namespace Probe { using Models; namespace Nested { BODY } }",
        "using Alias = System.Int32; namespace Probe { using Alias = Models.Word; using Models; BODY }",
        "namespace Probe { using static Models.Extensions; BODY }")] string scope)
    {
        string source = "using Nethermind.Serialization.FluentRlp; " + scope.Replace("BODY",
            "[RlpSerializable] public record Item(global::Probe.Models.Word Value);");
        string models = """
            using Nethermind.Serialization.FluentRlp;
            namespace Probe.Models;
            public record Word(int Value);
            public static class Extensions
            {
                public static Word ReadWord(this ref RlpReader reader) => new(reader.ReadInteger<int>());
                public static void Write(this ref RlpWriter writer, Word value) => writer.Write(value.Value);
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source, out Compilation compilation, models);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        }
    }

    private static GeneratorDriverRunResult RunGenerator(string source, out Compilation output, params string[] additionalSources)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "RlpGeneratorDiagnostics",
            [CSharpSyntaxTree.ParseText(source, parseOptions), .. additionalSources.Select(s => CSharpSyntaxTree.ParseText(s, parseOptions))],
            MetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(CreateGenerator()).WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out _);
        return driver.GetRunResult();
    }

    private static IIncrementalGenerator CreateGenerator()
    {
        DirectoryInfo configurationDirectory = new(AppContext.BaseDirectory);
        DirectoryInfo artifactsBinDirectory = configurationDirectory.Parent?.Parent
            ?? throw new InvalidOperationException($"Cannot resolve artifacts/bin from {AppContext.BaseDirectory}");
        string generatorAssemblyPath = Path.Combine(
            artifactsBinDirectory.FullName,
            "Nethermind.Serialization.FluentRlp.Generator",
            configurationDirectory.Name,
            "Nethermind.Serialization.FluentRlp.Generator.dll");

        Assembly assembly = Assembly.LoadFrom(generatorAssemblyPath);
        Type generatorType = assembly.GetType("Nethermind.Serialization.FluentRlp.Generator.RlpSourceGenerator", throwOnError: true)!;
        return (IIncrementalGenerator)Activator.CreateInstance(generatorType)!;
    }

    private static MetadataReference[] MetadataReferences()
    {
        string trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        return
        [
            .. trustedPlatformAssemblies.Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(RlpSerializableAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(UInt256).Assembly.Location),
        ];
    }
}
