// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
            using Nethermind.Serialization.Ssz;

            [SszContainer]
            public partial struct BadContainer
            {
                public BadFixedBytes Value { get; set; }
            }

            public readonly struct BadFixedBytes
            {
            }

            public sealed class BadFixedBytesConverter : SszVectorConverter<BadFixedBytes>
            {
                public static int Length => 4;

                public static BadFixedBytes FromSpan(ReadOnlySpan<byte> span) => default;

                public static void ToSpan(Span<byte> span, BadFixedBytes value)
                {
                }
            }
            """;

        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        CSharpCompilation compilation = CSharpCompilation.Create(
            nameof(Converter_without_public_const_length_reports_diagnostic),
            [syntaxTree],
            BuildMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(CreateSszGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGenerators(compilation);

        Diagnostic? diagnostic = null;
        foreach (Diagnostic candidate in driver.GetRunResult().Diagnostics)
        {
            if (candidate.Id == "SSZ003")
            {
                diagnostic = candidate;
                break;
            }
        }

        Assert.That(diagnostic, Is.Not.Null);
        Assert.That(diagnostic!.GetMessage(), Does.Contain("public const int Length"));
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
        MetadataReference[] references = new MetadataReference[platformAssemblyPaths.Length + 1];
        for (int i = 0; i < platformAssemblyPaths.Length; i++)
        {
            references[i] = MetadataReference.CreateFromFile(platformAssemblyPaths[i]);
        }

        references[^1] = MetadataReference.CreateFromFile(typeof(SszContainerAttribute).Assembly.Location);
        return references;
    }
}
