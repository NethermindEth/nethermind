// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
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
public record Integers(short A, int B, long C, Int128 D);

[RlpSerializable]
public record UnsignedIntegers(byte A, ushort B, uint C, ulong D, UInt128 E, UInt256 F);

[RlpSerializable]
public record IntegerTuple((int, long) Values);

[RlpSerializable(RlpRepresentation.Newtype, length: 32)]
public record FixedKey(byte[] Bytes);

[RlpSerializable]
public record FixedEntry([RlpFixedLength(20)] byte[] Address, List<FixedKey> Keys);

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

    [Test]
    public void RecordWithUnsignedIntegerLimits()
    {
        UnsignedIntegers integers = new(byte.MaxValue, ushort.MaxValue, uint.MaxValue, ulong.MaxValue, UInt128.MaxValue, UInt256.MaxValue);
        byte[] rlp = Rlp.Write(integers, static (ref RlpWriter w, UnsignedIntegers value) => w.Write(value));

        UnsignedIntegers decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadUnsignedIntegers());
        Assert.That(decoded, Is.EqualTo(integers));
    }

    [Test]
    public void RecordWithFixedLengthRejectsOtherLengths()
    {
        byte[] shortRlp = Rlp.Write(static (ref RlpWriter w) => w.Write(new byte[FixedAddress.Size - 1]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => Rlp.Read(shortRlp, static (scoped ref RlpReader r) => r.ReadFixedAddress()), Throws.TypeOf<RlpReaderException>());
            Assert.That(() => Rlp.Write(new FixedAddress(new byte[FixedAddress.Size + 1]), static (ref RlpWriter w, FixedAddress value) => w.Write(value)),
                Throws.ArgumentException);
        }
    }

    [Test]
    public void RecordWithFixedLengthHints([Values(0, 1, 3)] int keyCount)
    {
        byte[] address = Enumerable.Range(1, 20).Select(static b => (byte)b).ToArray();
        List<byte[]> keys = Enumerable.Range(0, keyCount).Select(static i => Enumerable.Repeat((byte)(i + 1), 32).ToArray()).ToList();
        FixedEntry entry = new(address, keys.Select(static k => new FixedKey(k)).ToList());

        byte[] rlp = Rlp.Write(entry, static (ref RlpWriter w, FixedEntry value) => w.Write(value));
        byte[] unhinted = Rlp.Write(new UnhintedEntry(address, keys), static (ref RlpWriter w, UnhintedEntry value) => w.Write(value));
        FixedEntry decoded = Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadFixedEntry());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rlp, Is.EqualTo(unhinted));
            Assert.That(decoded.Address, Is.EqualTo(address));
            Assert.That(decoded.Keys.Select(static k => k.Bytes), Is.EqualTo(keys));
            Assert.That(() => Rlp.Write(entry with { Address = new byte[19] }, static (ref RlpWriter w, FixedEntry value) => w.Write(value)),
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
        => Assert.That(static () => Rlp.Write<FixedEntryRlpConverter, FixedEntry>(new FixedEntry(new byte[19], [])), Throws.InvalidOperationException);

    private static void AssertDirect<TConverter, T>(T value) where TConverter : IRlpLengthConverter<T>
    {
        byte[] expected = Rlp.Write(value, static (ref RlpWriter w, T value) => TConverter.Write(ref w, value));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Rlp.Write<TConverter, T>(value), Is.EqualTo(expected), typeof(T).Name);
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

        Assert.That(() => Rlp.Read(rlp, static (scoped ref RlpReader r) => r.ReadFixedEntry()), Throws.TypeOf<RlpReaderException>());
    }

    [TestCase("public record Account(int Nonce) { public int Balance { get; init; } }", "RLP0002", TestName = "Settable property in the body")]
    [TestCase("public record Account(int Nonce) { public int Balance; }", "RLP0002", TestName = "Field in the body")]
    [TestCase("public record Account { public int Balance { get; init; } }", "RLP0003", TestName = "No primary constructor")]
    [TestCase("public record Wrapper<T>(T Value);", "RLP0003", TestName = "Generic record")]
    [TestCase("public record Outer(int A) { [RlpSerializable] public record Inner(int B); }", "RLP0003", TestName = "Nested record")]
    [TestCase("[RlpSerializable(RlpRepresentation.Newtype)] public record Pair(int A, int B);", "RLP0001", TestName = "Newtype with two parameters")]
    [TestCase("[RlpSerializable(length: 20)] public record Number(int A);", "RLP0005", TestName = "Length without a byte array")]
    [TestCase("public record Triple((int, int, int) A);", "RLP0004", TestName = "Tuple of three")]
    [TestCase("public record Account(int Nonce) { public int Twice => Nonce * 2; public const int Size = 1; public int Nonce { get; init; } = Nonce; }", null, TestName = "Derived and redeclared members")]
    [TestCase("public partial record Account(int Nonce); public partial record Account { public int Balance { get; init; } }", "RLP0002", TestName = "Settable property in another part")]
    [TestCase("public record Account(int Nonce) { public int Nonce { get; init; } = Nonce * 2; }", "RLP0002", TestName = "Redeclared parameter property with another value")]
    [TestCase("public record Base { public int Balance { get; init; } } [RlpSerializable] public record Account(int Nonce) : Base;", "RLP0003", TestName = "Derived record")]
    [TestCase("public record Flags(bool A);", "RLP0004", TestName = "Bool parameter")]
    [TestCase("public enum Kind { A } [RlpSerializable] public record Tagged(Kind A);", "RLP0004", TestName = "Enum parameter")]
    [TestCase("public record Keys(System.Collections.Generic.Dictionary<byte[], int> A);", "RLP0004", TestName = "Byte array dictionary key")]
    [TestCase("[RlpSerializable(RlpRepresentation.Newtype)] public record Loop(Loop Inner);", "RLP0006", TestName = "Recursive Newtype")]
    [TestCase("[RlpSerializable(RlpRepresentation.Newtype)] public record Loop((int, Loop) Inner);", "RLP0006", TestName = "Recursive Newtype through a tuple")]
    [TestCase("internal record Hidden(int A);", null, TestName = "Internal record")]
    [TestCase("public record Names(int @class, string value, int r, int w, System.Collections.Generic.List<System.Collections.Generic.List<int>> items, int _0);", null, TestName = "Keyword and generated names as parameters")]
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
            Assert.That(compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        }
        else
        {
            Assert.That(ids, Does.Contain(expectedId));
        }
    }

    private static GeneratorDriverRunResult RunGenerator(string source, out Compilation output)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "RlpGeneratorDiagnostics",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
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
