// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using System;
using System.IO;
using System.Reflection;

namespace Nethermind.Serialization.SszGenerator.Test;

public class SszGeneratorDiagnosticTest
{
    [Test]
    public void Types_with_equal_names_in_different_namespaces_generate_distinct_codecs()
    {
        const string source = """
            using Nethermind.Serialization.Ssz;
            namespace First
            {
                [SszContainer]
                public partial struct Item { public byte Value { get; set; } }
            }
            namespace Second
            {
                [SszContainer]
                public partial struct Item { public ulong Value { get; set; } }
            }
            [SszContainer]
            public partial struct Container
            {
                public First.Item First { get; set; }
                public Second.Item Second { get; set; }
            }
            """;

        AssertGeneratedEncodingLength(source, nameof(Types_with_equal_names_in_different_namespaces_generate_distinct_codecs), 9);
    }

    [Test]
    public void Converters_for_generic_arguments_in_different_namespaces_remain_distinct()
    {
        string source = """
            using System;
            using Nethermind.Serialization.Ssz;
            using Nethermind.Serialization.Ssz.Merkleization;
            namespace First { public struct Item { } }
            namespace Second { public struct Item { } }
            public struct Wrapper<T> { }
            [SszContainer]
            public partial struct Container
            {
                public Wrapper<First.Item> First { get; set; }
                public Wrapper<Second.Item> Second { get; set; }
            }
            """;
        source += ConverterSource("First", 1) + ConverterSource("Second", 8);

        AssertGeneratedEncodingLength(source, nameof(Converters_for_generic_arguments_in_different_namespaces_remain_distinct), 9);
    }

    private static string ConverterSource(string name, int length) => $$"""

        [SszVectorTypeConverter<Wrapper<{{name}}.Item>>]
        public static class {{name}}Converter
        {
            public const int Length = {{length}};
            public static Wrapper<{{name}}.Item> FromSpan(ReadOnlySpan<byte> span) => default;
            public static void FromSpan(ReadOnlySpan<byte> span, Span<Wrapper<{{name}}.Item>> values) => values.Clear();
            public static void ToSpan(Span<byte> span, Wrapper<{{name}}.Item> value) => span.Clear();
            public static void ToSpan(Span<byte> span, ReadOnlySpan<Wrapper<{{name}}.Item>> values) => span.Clear();
            public static void Feed(ref Merkleizer merkleizer, Wrapper<{{name}}.Item> value) => merkleizer.Feed(default);
        }
        """;

    private static void AssertGeneratedEncodingLength(string source, string assemblyName, int expectedLength)
    {
        GeneratorDriverRunResult result = RunGenerator(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), assemblyName, out Compilation output);
        Assert.That(result.Diagnostics, Is.Empty);
        using MemoryStream assemblyStream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emit = output.Emit(assemblyStream);
        Assert.That(emit.Success, Is.True, string.Join("\n", emit.Diagnostics));
        Assembly assembly = Assembly.Load(assemblyStream.ToArray());
        Type containerType = assembly.GetType("Container", throwOnError: true)!;
        object value = Activator.CreateInstance(containerType)!;
        byte[] bytes = (byte[])containerType.GetMethod("Encode", [containerType])!.Invoke(null, [value])!;
        Assert.That(bytes, Is.EqualTo(new byte[expectedLength]));
    }

    [Test]
    public void Converter_without_public_const_length_reports_diagnostic()
    {
        const string source = """
            using System;
            using Nethermind.Serialization.Ssz.Merkleization;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct BadContainer
            {
                public BadFixedBytes Value { get; set; }
            }

            public readonly struct BadFixedBytes
            {
            }

            [SszVectorTypeConverter<BadFixedBytes>]
            public static class BadFixedBytesConverter
            {
                public static int Length => 4;

                public static BadFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void FromSpan(ReadOnlySpan<byte> span, Span<BadFixedBytes> values)
                {
                }

                public static void ToSpan(Span<byte> span, BadFixedBytes value)
                {
                }

                public static void ToSpan(Span<byte> span, ReadOnlySpan<BadFixedBytes> values)
                {
                }

                public static void Feed(ref Merkleizer merkleizer, BadFixedBytes value)
                {
                }
            }
            """;

        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        Diagnostic diagnostic = GetSsz003Diagnostic(source, parseOptions, nameof(Converter_without_public_const_length_reports_diagnostic));
        Assert.That(diagnostic.GetMessage(), Does.Contain("public const int Length"));
    }

    [Test]
    public void Converter_without_from_span_reports_diagnostic()
    {
        const string source = """
            using System;
            using Nethermind.Serialization.Ssz.Merkleization;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct BadContainer
            {
                public BadFixedBytes Value { get; set; }
            }

            public readonly struct BadFixedBytes
            {
            }

            [SszVectorTypeConverter<BadFixedBytes>]
            public static class BadFixedBytesConverter
            {
                public const int Length = 4;

                public static void ToSpan(Span<byte> span, BadFixedBytes value)
                {
                }

                public static void ToSpan(Span<byte> span, ReadOnlySpan<BadFixedBytes> values)
                {
                }

                public static void Feed(ref Merkleizer merkleizer, BadFixedBytes value)
                {
                }
            }
            """;

        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        Diagnostic diagnostic = GetSsz003Diagnostic(source, parseOptions, nameof(Converter_without_from_span_reports_diagnostic));
        Assert.That(diagnostic.GetMessage(), Does.Contain("FromSpan"));
    }

    [Test]
    public void Converter_without_feed_reports_diagnostic()
    {
        const string source = """
            using System;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct BadContainer
            {
                public BadFixedBytes Value { get; set; }
            }

            public readonly struct BadFixedBytes
            {
            }

            [SszVectorTypeConverter<BadFixedBytes>]
            public static class BadFixedBytesConverter
            {
                public const int Length = 4;

                public static BadFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void FromSpan(ReadOnlySpan<byte> span, Span<BadFixedBytes> values)
                {
                }

                public static void ToSpan(Span<byte> span, BadFixedBytes value)
                {
                }

                public static void ToSpan(Span<byte> span, ReadOnlySpan<BadFixedBytes> values)
                {
                }
            }
            """;

        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        Diagnostic diagnostic = GetSsz003Diagnostic(source, parseOptions, nameof(Converter_without_feed_reports_diagnostic));
        Assert.That(diagnostic.GetMessage(), Does.Contain("Feed"));
    }

    [Test]
    public void Duplicate_converters_for_same_type_report_diagnostic()
    {
        const string source = """
            using System;
            using Nethermind.Serialization.Ssz.Merkleization;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct DuplicateContainer
            {
                public DuplicateFixedBytes Value { get; set; }
            }

            public readonly struct DuplicateFixedBytes
            {
            }

            [SszVectorTypeConverter<DuplicateFixedBytes>]
            public static class FirstDuplicateFixedBytesConverter
            {
                public const int Length = 4;

                public static DuplicateFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void FromSpan(ReadOnlySpan<byte> span, Span<DuplicateFixedBytes> values)
                {
                }

                public static void ToSpan(Span<byte> span, DuplicateFixedBytes value)
                {
                }

                public static void ToSpan(Span<byte> span, ReadOnlySpan<DuplicateFixedBytes> values)
                {
                }

                public static void Feed(ref Merkleizer merkleizer, DuplicateFixedBytes value)
                {
                }
            }

            [SszVectorTypeConverter<DuplicateFixedBytes>]
            public static class SecondDuplicateFixedBytesConverter
            {
                public const int Length = 4;

                public static DuplicateFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void FromSpan(ReadOnlySpan<byte> span, Span<DuplicateFixedBytes> values)
                {
                }

                public static void ToSpan(Span<byte> span, DuplicateFixedBytes value)
                {
                }

                public static void ToSpan(Span<byte> span, ReadOnlySpan<DuplicateFixedBytes> values)
                {
                }

                public static void Feed(ref Merkleizer merkleizer, DuplicateFixedBytes value)
                {
                }
            }
            """;

        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        Diagnostic diagnostic = GetSsz003Diagnostic(source, parseOptions, nameof(Duplicate_converters_for_same_type_report_diagnostic));
        Assert.That(diagnostic.GetMessage(), Does.Contain("Multiple SSZ converters"));
    }

    [Test]
    public void Bitlist_with_limit_beyond_int_range_reports_diagnostic()
    {
        const string source = """
            using System.Collections;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct HugeBitlistContainer
            {
                [SszList(1_099_511_627_776)]
                public BitArray? Bits { get; set; }
            }
            """;

        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        Diagnostic diagnostic = GetSsz003Diagnostic(source, parseOptions, nameof(Bitlist_with_limit_beyond_int_range_reports_diagnostic));
        Assert.That(diagnostic.GetMessage(), Does.Contain("BitArray cannot exceed int.MaxValue bits"));
    }

    [Test]
    public void Converter_backed_primitive_collections_emit_converter_calls()
    {
        const string source = """
            using System;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct PrimitiveCollectionContainer
            {
                [SszVector(3)]
                public bool[]? Bools { get; set; }

                [SszVector(2)]
                public int[]? Ints { get; set; }

                [SszList(2)]
                public long[]? Longs { get; set; }

                [SszVector(2)]
                public UInt128[]? Wides { get; set; }

                [SszVector(2)]
                public PrimitiveEnum[]? Enums { get; set; }
            }

            public enum PrimitiveEnum : uint
            {
                A = 1,
                B = 2,
            }
            """;

        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        string generated = GetGeneratedSource(source, parseOptions, nameof(Converter_backed_primitive_collections_emit_converter_calls), "Serialization.SszCodec.PrimitiveCollectionContainer.cs");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Contain("BooleanSszBasicTypeConverter.ToSpan"));
            Assert.That(generated, Does.Contain("BooleanSszBasicTypeConverter.FromSpan"));
            Assert.That(generated, Does.Not.Contain("ValidateBooleans"));
            Assert.That(generated, Does.Not.Contain("ValidateBoolean"));
            Assert.That(generated, Does.Contain("Int32SszBasicTypeConverter.ToSpan"));
            Assert.That(generated, Does.Contain("Int32SszBasicTypeConverter.FromSpan"));
            Assert.That(generated, Does.Contain("MerkleizeBasicVectorWithConverter<int>"));
            Assert.That(generated, Does.Contain("Int64SszBasicTypeConverter.ToSpan"));
            Assert.That(generated, Does.Contain("Int64SszBasicTypeConverter.FromSpan"));
            Assert.That(generated, Does.Contain("MerkleizeBasicListWithConverter<long>"));
            Assert.That(generated, Does.Contain("UInt128SszBasicTypeConverter.ToSpan"));
            Assert.That(generated, Does.Contain("UInt128SszBasicTypeConverter.FromSpan"));
            Assert.That(generated, Does.Contain("MerkleizeBasicVectorWithConverter<global::System.UInt128>"));
            Assert.That(generated, Does.Contain("UInt32SszBasicTypeConverter.ToSpan"));
            Assert.That(generated, Does.Contain("UInt32SszBasicTypeConverter.FromSpan"));
            Assert.That(generated, Does.Contain("MerkleizeBasicVectorWithConverter<uint>"));
            Assert.That(generated, Does.Not.Contain("SszBasicTypeConverter.MerkleizeVector"));
            Assert.That(generated, Does.Not.Contain("SszBasicTypeConverter.MerkleizeList"));
            Assert.That(generated, Does.Not.Contain("SszBasicTypeConverter.MerkleizeProgressiveList"));
            Assert.That(generated, Does.Contain("MemoryMarshal.Cast<global::PrimitiveEnum, uint>"));
            Assert.That(generated, Does.Not.Contain("EncodeItemsWithConverter"));
            Assert.That(generated, Does.Not.Contain("DecodeItemsWithConverter"));
            Assert.That(generated, Does.Not.Contain("SszLib.Encode"));
            Assert.That(generated, Does.Not.Contain("SszLib.Decode"));
            Assert.That(generated, Does.Not.Contain("using Nethermind.Core;"));
            Assert.That(generated, Does.Not.Contain("using Nethermind.Core.Crypto;"));
            Assert.That(generated, Does.Not.Contain("using Nethermind.Serialization.Ssz.SszBasicTypeConverters;"));
        }
    }

    private static Diagnostic GetSsz003Diagnostic(string source, CSharpParseOptions parseOptions, string assemblyName)
    {
        GeneratorDriverRunResult result = RunGenerator(source, parseOptions, assemblyName, out _);

        foreach (Diagnostic candidate in result.Diagnostics)
        {
            if (candidate.Id == "SSZ003")
            {
                return candidate;
            }
        }

        Assert.Fail("Expected SSZ003 diagnostic.");
        return null!;
    }

    private static string GetGeneratedSource(string source, CSharpParseOptions parseOptions, string assemblyName, string hintName)
    {
        GeneratorDriverRunResult result = RunGenerator(source, parseOptions, assemblyName, out _);

        foreach (SyntaxTree generatedTree in result.GeneratedTrees)
        {
            if (Path.GetFileName(generatedTree.FilePath) == hintName)
            {
                return generatedTree.GetText().ToString();
            }
        }

        Assert.Fail($"Expected generated source {hintName}.");
        return string.Empty;
    }

    private static GeneratorDriverRunResult RunGenerator(string source, CSharpParseOptions parseOptions, string assemblyName, out Compilation output)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(CreateSszGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out _);

        return driver.GetRunResult();
    }

    private static IIncrementalGenerator CreateSszGenerator()
    {
        DirectoryInfo configurationDirectory = new(AppContext.BaseDirectory);
        DirectoryInfo artifactsBinDirectory = configurationDirectory.Parent?.Parent
            ?? throw new InvalidOperationException($"Cannot resolve artifacts/bin from {AppContext.BaseDirectory}");
        string generatorAssemblyPath = Path.Combine(
            artifactsBinDirectory.FullName,
            "Nethermind.Serialization.SszGenerator",
            configurationDirectory.Name,
            "Nethermind.Serialization.SszGenerator.dll");

        Assembly assembly = Assembly.LoadFrom(generatorAssemblyPath);
        Type generatorType = assembly.GetType("SszGenerator", throwOnError: true)!;
        return (IIncrementalGenerator)Activator.CreateInstance(generatorType)!;
    }

    private static MetadataReference[] BuildMetadataReferences()
    {
        string? trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        Assert.That(trustedPlatformAssemblies, Is.Not.Null);

        string[] platformAssemblyPaths = trustedPlatformAssemblies!.Split(Path.PathSeparator);
        MetadataReference[] references = new MetadataReference[platformAssemblyPaths.Length + 3];
        for (int i = 0; i < platformAssemblyPaths.Length; i++)
        {
            references[i] = MetadataReference.CreateFromFile(platformAssemblyPaths[i]);
        }

        references[^3] = MetadataReference.CreateFromFile(typeof(SszContainerAttribute).Assembly.Location);
        references[^2] = MetadataReference.CreateFromFile(typeof(SszVectorTypeConverterAttribute<>).Assembly.Location);
        references[^1] = MetadataReference.CreateFromFile(typeof(UInt256).Assembly.Location);
        return references;
    }
}
