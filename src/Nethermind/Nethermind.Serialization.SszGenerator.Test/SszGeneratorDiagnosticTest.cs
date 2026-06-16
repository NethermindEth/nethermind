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
            Assert.That(generated, Does.Contain("MerkleizeBasicVectorWithConverter<UInt128>"));
            Assert.That(generated, Does.Contain("UInt32SszBasicTypeConverter.ToSpan"));
            Assert.That(generated, Does.Contain("UInt32SszBasicTypeConverter.FromSpan"));
            Assert.That(generated, Does.Contain("MerkleizeBasicVectorWithConverter<uint>"));
            Assert.That(generated, Does.Not.Contain("SszBasicTypeConverter.MerkleizeVector"));
            Assert.That(generated, Does.Not.Contain("SszBasicTypeConverter.MerkleizeList"));
            Assert.That(generated, Does.Not.Contain("SszBasicTypeConverter.MerkleizeProgressiveList"));
            Assert.That(generated, Does.Contain("MemoryMarshal.Cast<PrimitiveEnum, uint>"));
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
        GeneratorDriverRunResult result = RunGenerator(source, parseOptions, assemblyName);

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
        GeneratorDriverRunResult result = RunGenerator(source, parseOptions, assemblyName);

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

    private static GeneratorDriverRunResult RunGenerator(string source, CSharpParseOptions parseOptions, string assemblyName)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(CreateSszGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGenerators(compilation);

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
