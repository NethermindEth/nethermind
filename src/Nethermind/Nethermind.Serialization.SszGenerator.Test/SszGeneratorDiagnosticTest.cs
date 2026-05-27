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
            using Nethermind.Merkleization;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct BadContainer
            {
                public BadFixedBytes Value { get; set; }
            }

            public readonly struct BadFixedBytes
            {
            }

            public sealed class BadFixedBytesConverter : ISszVectorConverter<BadFixedBytes>
            {
                public static int Length => 4;

                public static BadFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void ToSpan(Span<byte> span, BadFixedBytes value)
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
            using Nethermind.Merkleization;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct BadContainer
            {
                public BadFixedBytes Value { get; set; }
            }

            public readonly struct BadFixedBytes
            {
            }

            public sealed class BadFixedBytesConverter : ISszVectorConverter<BadFixedBytes>
            {
                public const int Length = 4;

                public static void ToSpan(Span<byte> span, BadFixedBytes value)
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

            public sealed class BadFixedBytesConverter : ISszVectorConverter<BadFixedBytes>
            {
                public const int Length = 4;

                public static BadFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void ToSpan(Span<byte> span, BadFixedBytes value)
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
            using Nethermind.Merkleization;
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct DuplicateContainer
            {
                public DuplicateFixedBytes Value { get; set; }
            }

            public readonly struct DuplicateFixedBytes
            {
            }

            public sealed class FirstDuplicateFixedBytesConverter : ISszVectorConverter<DuplicateFixedBytes>
            {
                public const int Length = 4;

                public static DuplicateFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void ToSpan(Span<byte> span, DuplicateFixedBytes value)
                {
                }

                public static void Feed(ref Merkleizer merkleizer, DuplicateFixedBytes value)
                {
                }
            }

            public sealed class SecondDuplicateFixedBytesConverter : ISszVectorConverter<DuplicateFixedBytes>
            {
                public const int Length = 4;

                public static DuplicateFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void ToSpan(Span<byte> span, DuplicateFixedBytes value)
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

    private static Diagnostic GetSsz003Diagnostic(string source, CSharpParseOptions parseOptions, string assemblyName)
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

        foreach (Diagnostic candidate in driver.GetRunResult().Diagnostics)
        {
            if (candidate.Id == "SSZ003")
            {
                return candidate;
            }
        }

        Assert.Fail("Expected SSZ003 diagnostic.");
        return null!;
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
        references[^2] = MetadataReference.CreateFromFile(typeof(ISszVectorConverter<>).Assembly.Location);
        references[^1] = MetadataReference.CreateFromFile(typeof(UInt256).Assembly.Location);
        return references;
    }
}
