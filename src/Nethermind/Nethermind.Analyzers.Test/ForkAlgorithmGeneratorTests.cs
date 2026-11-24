// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;

using Nethermind.BeaconChain.Generators;

namespace Nethermind.Analyzers.Test;

public class ForkAlgorithmGeneratorTests
{
    [Test]
    public void Forks_have_concrete_types_and_only_their_active_statements()
    {
        const string template = """
            public static partial class BlockProcessing
            {
            #if GLOAS
                internal
            #else
                public
            #endif
                static partial ForkState Run(ForkBody body, ForkAttesterSlashing slash, ForkIndexedAttestation vote, ForkBlock block, ForkAttestation attestation)
                {
                    // ForkState, ForkBody and ForkSignatureSets are literal documentation here.
                    const string stateName = "ForkState";
                    const string bodyName = @"ForkBody ForkBlock ForkAttestation";
                    const char letter = 'F';
                    ForkSignatureSets.Verify();
                    ForkEpochProcessing.ProcessEpoch();
            #if GLOAS
                    BlockProcessing.EnsureSyncCommitteeWidth(body);
                    return new ForkState { Slot = 2 };
            #else
                    return new ForkState { Slot = 1 };
            #endif
                }
            }
            """;
        GeneratorDriverRunResult result = Run(new Template("BlockProcessing.forks.cs", template));
        Assert.That(result.Diagnostics, Is.Empty);
        GeneratedSourceResult[] sources = result.Results.Single().GeneratedSources.ToArray();
        Assert.That(sources, Has.Length.EqualTo(2));
        foreach (GeneratedSourceResult source in sources)
        {
            bool gloas = source.HintName.StartsWith("Gloas", StringComparison.Ordinal);
            string text = source.SourceText.ToString();
            using IDisposable scope = Assert.EnterMultipleScope();
            Assert.That(source.SyntaxTree.GetDiagnostics(), Is.Empty);
            Assert.That(text, Does.Contain(gloas ? "class GloasBlockProcessing" : "class BlockProcessing"));
            MethodDeclarationSyntax method = source.SyntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
            Assert.That(method.Modifiers.Any(token => token.IsKind(gloas ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword)), Is.True);
            Assert.That(method.Modifiers.Any(token => token.IsKind(SyntaxKind.PartialKeyword)), Is.True);
            Assert.That(text, Does.Contain(gloas ? "BeaconStateGloas" : "BeaconStateFulu"));
            Assert.That(text, Does.Contain(gloas ? "BeaconBlockBodyGloas" : "BeaconBlockBody body"));
            Assert.That(text, Does.Contain(gloas ? "AttesterSlashingGloas" : "AttesterSlashing slash"));
            Assert.That(text, Does.Contain(gloas ? "IndexedAttestationGloas" : "IndexedAttestation vote"));
            Assert.That(text, Does.Contain(gloas ? "BeaconBlockGloas block" : "BeaconBlock block"));
            Assert.That(text, Does.Contain(gloas ? "AttestationGloas attestation" : "Attestation attestation"));
            Assert.That(text, Does.Contain(gloas ? "GloasSignatureSets.Verify" : "SignatureSets.Verify"));
            Assert.That(text, Does.Contain(gloas ? "GloasEpochProcessing.ProcessEpoch" : "EpochProcessing.ProcessEpoch"));
            Assert.That(text, Does.Contain(gloas ? "Slot = 2" : "Slot = 1"));
            Assert.That(text, Does.Not.Contain(gloas ? "Slot = 1" : "Slot = 2"));
            Assert.That(text.Contains("BlockProcessing.EnsureSyncCommitteeWidth", StringComparison.Ordinal), Is.EqualTo(gloas));
            Assert.That(text, Does.Not.Contain("#if").And.Not.Contain("#else").And.Not.Contain("#endif"));
            Assert.That(text, Does.Contain("// ForkState, ForkBody and ForkSignatureSets are literal documentation here."));
            Assert.That(text, Does.Contain("const string stateName = \"ForkState\";"));
            Assert.That(text, Does.Contain("const string bodyName = @\"ForkBody ForkBlock ForkAttestation\";"));
            Assert.That(text, Does.Contain("const char letter = 'F';"));
        }
    }

    [Test]
    public void Generated_partial_bodies_preserve_declaration_order_defaults_and_attributes()
    {
        const string declarations = """
            public static partial class BlockProcessing
            {
                public static int Before() => 1;
                [System.Obsolete("declaration contract")]
                public static partial int PublicMoved(int value = 7);
                public static int Between() => 3;
                private static partial int PrivateMoved(int value = 9);
                public static int After() => 5;
            }
            """;
        const string template = """
            public static partial class BlockProcessing
            {
                private static partial int PrivateMoved(int value) => value;
                public static partial int PublicMoved(int value) => value;
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create("PartialForkContracts",
            syntaxTrees: [CSharpSyntaxTree.ParseText(declarations), CSharpSyntaxTree.ParseText(declarations.Replace("BlockProcessing", "GloasBlockProcessing", StringComparison.Ordinal))],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new ForkAlgorithmGenerator().AsSourceGenerator()], additionalTexts: [new Template("BlockProcessing.forks.cs", template)]);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation generated, out ImmutableArray<Diagnostic> diagnostics);
        Assert.That(diagnostics, Is.Empty);
        using MemoryStream image = new();
        EmitResult emitted = generated.Emit(image);
        Assert.That(emitted.Success, Is.True, string.Join(Environment.NewLine, emitted.Diagnostics));
        Assembly assembly = Assembly.Load(image.ToArray());
        foreach (string name in new[] { "BlockProcessing", "GloasBlockProcessing" })
        {
            MethodInfo[] methods = assembly.GetType(name)!.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .OrderBy(static method => method.MetadataToken).ToArray();
            using IDisposable assertionScope = Assert.EnterMultipleScope();
            Assert.That(methods.Select(static method => method.Name), Is.EqualTo(new[] { "Before", "PublicMoved", "Between", "PrivateMoved", "After" }));
            Assert.That(methods[1].IsPublic, Is.True);
            Assert.That(methods[3].IsPrivate, Is.True);
            Assert.That(methods[1].ReturnType, Is.EqualTo(typeof(int)));
            Assert.That(methods[3].ReturnType, Is.EqualTo(typeof(int)));
            Assert.That(methods[1].GetParameters().Single().IsOptional, Is.True);
            Assert.That(methods[1].GetParameters().Single().DefaultValue, Is.EqualTo(7));
            Assert.That(methods[3].GetParameters().Single().IsOptional, Is.True);
            Assert.That(methods[3].GetParameters().Single().DefaultValue, Is.EqualTo(9));
            Assert.That(methods[1].GetCustomAttributes<ObsoleteAttribute>().Single().Message, Is.EqualTo("declaration contract"));
            Assert.That(methods[1].Invoke(null, [17]), Is.EqualTo(17));
            Assert.That(methods[3].Invoke(null, [19]), Is.EqualTo(19));
        }
    }

    [Test]
    public void Fork_list_from_the_build_property_specializes_each_fork_with_the_symbols_up_to_it()
    {
        const string template = """
            public static partial class BlockProcessing
            {
                public static ForkState Run(ForkBody body)
                {
                    ForkSignatureSets.Verify();
            #if HEZE
                    return new ForkState { Slot = 3 };
            #elif GLOAS
                    return new ForkState { Slot = 2 };
            #else
                    return new ForkState { Slot = 1 };
            #endif
                }
            }
            """;
        GeneratorDriverRunResult result = Run(new Template("BlockProcessing.forks.cs", template), "Fulu, Gloas,Heze");
        Assert.That(result.Diagnostics, Is.Empty);
        string[] expected =
        [
            "class BlockProcessing|BeaconStateFulu Run(BeaconBlockBody body)|SignatureSets.Verify|Slot = 1",
            "class GloasBlockProcessing|BeaconStateGloas Run(BeaconBlockBodyGloas body)|GloasSignatureSets.Verify|Slot = 2",
            "class HezeBlockProcessing|BeaconStateHeze Run(BeaconBlockBodyHeze body)|HezeSignatureSets.Verify|Slot = 3",
        ];
        GeneratedSourceResult[] sources = result.Results.Single().GeneratedSources.ToArray();
        Assert.That(sources.Select(static source => source.HintName), Is.EqualTo(new[] { "BlockProcessing.forks.g.cs", "GloasBlockProcessing.forks.g.cs", "HezeBlockProcessing.forks.g.cs" }));
        for (int i = 0; i < sources.Length; i++)
        {
            string text = sources[i].SourceText.ToString();
            using IDisposable scope = Assert.EnterMultipleScope();
            Assert.That(sources[i].SyntaxTree.GetDiagnostics(), Is.Empty);
            foreach (string fragment in expected[i].Split('|'))
                Assert.That(text, Does.Contain(fragment));
            Assert.That(text.Split("Slot = ").Length, Is.EqualTo(2));
        }
    }

    [Test]
    public void Unrelated_additional_files_are_ignored() =>
        Assert.That(Run(new Template("other.cs", "invalid input")).Results.Single().GeneratedSources, Is.Empty);

    [Test]
    public void Invalid_template_reports_syntax_errors() =>
        Assert.That(Run(new Template("BlockProcessing.forks.cs", "class { }")).Diagnostics,
            Has.Some.Matches<Diagnostic>(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    private static GeneratorDriverRunResult Run(AdditionalText template, string forks = null)
    {
        CSharpCompilation compilation = CSharpCompilation.Create("ForkGeneratorTests");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new ForkAlgorithmGenerator().AsSourceGenerator()], additionalTexts: [template],
            optionsProvider: forks is null ? null : new BuildProperties(forks));
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private sealed class BuildProperties(string forks) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(forks);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options(null);
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(null);

        private sealed class Options(string forks) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            {
                value = key == "build_property.BeaconForkTemplates" ? forks : null;
                return value is not null;
            }
        }
    }

    private sealed class Template(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
