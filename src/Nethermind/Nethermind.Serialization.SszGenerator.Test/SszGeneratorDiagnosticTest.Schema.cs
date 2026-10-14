// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Nethermind.Serialization.SszGenerator.Test;

public partial class SszGeneratorDiagnosticTest
{
    [Test]
    public void Runtime_type_names_cannot_be_shadowed_by_the_consuming_namespace([Values] bool progressive)
    {
        string source = $$"""
            using Nethermind.Serialization.Ssz;
            public class Span<T> { }
            public class ReadOnlySpan<T> { }
            public class UInt256 { }
            public class Merkle { }
            public class Merkleizer { }
            public class Math { }
            public interface ISszCodec<T> { }
            [SszContainer]
            public partial struct Container { {{(progressive ? "[SszField(32)]" : string.Empty)}} public ulong Value { get; set; } }
            """;

        AssertGeneratedEncodingLength(source, nameof(Runtime_type_names_cannot_be_shadowed_by_the_consuming_namespace), 8);
    }

    [Test]
    public void Property_names_cannot_collide_with_decoder_locals()
    {
        const string source = """
            using Nethermind.Serialization.Ssz;
            [SszContainer]
            public partial struct Child { public byte Value { get; set; } }
            [SszContainer]
            public partial struct Container
            {
                public Child data { get; set; }
                [SszList(1)] public byte[] Items { get; set; }
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Property_names_cannot_collide_with_decoder_locals), out Compilation output);
        Assert.That(result.Diagnostics, Is.Empty);
        Assert.That(output.GetDiagnostics(), Has.None.Matches<Diagnostic>(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [TestCase("ISszCodec<T>")]
    [TestCase("ICodec<T>")]
    public void Direct_and_inherited_codec_constraints_generate_valid_code(string constraint)
    {
        string source = $$"""
            using Nethermind.Serialization.Ssz;
            public interface ICodec<T> : ISszCodec<T> where T : ISszCodec<T> { }
            [SszContainer]
            public partial struct Container<T> where T : struct, {{constraint}} { public T Value { get; set; } }
            """;
        GeneratorDriverRunResult result = RunGenerator(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Direct_and_inherited_codec_constraints_generate_valid_code), out Compilation output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(output.GetDiagnostics(), Has.None.Matches<Diagnostic>(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    [TestCase("[SszContainer] public partial struct Container<T> { public T Value { get; set; } }", "must be constrained to ISszCodec<T>")]
    [TestCase("[SszContainer] public partial struct Container { }", "must declare at least one serializable property")]
    public void Unrepresentable_schemas_report_a_generation_diagnostic(string declaration, string message)
    {
        string source = "using Nethermind.Serialization.Ssz;\n" + declaration;
        Diagnostic diagnostic = GetSsz003Diagnostic(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Unrepresentable_schemas_report_a_generation_diagnostic));

        Assert.That(diagnostic.GetMessage(), Does.Contain(message));
    }

    [TestCase("[SszContainer] public partial class Node { public Node Next { get; set; } }")]
    [TestCase("[SszContainer] public partial class Left { public Right Next { get; set; } } [SszContainer] public partial class Right { public Left Next { get; set; } }")]
    [TestCase("[SszContainer] public partial class Node<T> { public Node<Node<T>> Next { get; set; } }")]
    public void Recursive_schemas_report_diagnostics_without_crashing_the_generator(string declarations)
    {
        string source = "using Nethermind.Serialization.Ssz;\n" + declarations;
        GeneratorDriverRunResult result = RunGenerator(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Recursive_schemas_report_diagnostics_without_crashing_the_generator), out _);

        Assert.That(result.Diagnostics, Is.Not.Empty);
        using (Assert.EnterMultipleScope())
        {
            foreach (GeneratorRunResult generator in result.Results)
                Assert.That(generator.Exception, Is.Null);

            foreach (Diagnostic diagnostic in result.Diagnostics)
            {
                Assert.That(diagnostic.Id, Is.EqualTo("SSZ003"));
                Assert.That(diagnostic.GetMessage(), Does.Contain("recursive SSZ schema"));
            }
        }
    }

    [Test]
    public void Finite_generic_nesting_is_not_a_recursive_schema()
    {
        const string source = """
            using Nethermind.Serialization.Ssz;
            [SszContainer]
            public partial struct Leaf { public ulong Value { get; set; } }
            [SszContainer]
            public partial struct Box<T> where T : struct, ISszCodec<T> { public T Value { get; set; } }
            [SszContainer]
            public partial struct Container { public Box<Box<Leaf>> Value { get; set; } }
            """;

        GeneratorDriverRunResult result = RunGenerator(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Finite_generic_nesting_is_not_a_recursive_schema), out Compilation output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(output.GetDiagnostics(), Has.None.Matches<Diagnostic>(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    [Test]
    public void Unrelated_attribute_with_the_same_name_does_not_generate_a_codec()
    {
        const string source = """
            using System;
            namespace Other;
            public class SszContainerAttribute : Attribute { }
            [SszContainer]
            public partial struct Container { public DateTime Value { get; set; } }
            """;

        GeneratorDriverRunResult result = RunGenerator(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Unrelated_attribute_with_the_same_name_does_not_generate_a_codec), out Compilation output);
        INamedTypeSymbol? container = output.GetTypeByMetadataName("Other.Container");

        Assert.That(container, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(container!.AllInterfaces, Is.Empty);
            Assert.That(output.GetDiagnostics(), Has.None.Matches<Diagnostic>(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    [Test]
    public void Aliased_container_attribute_generates_a_codec()
    {
        const string source = """
            using Codec = Nethermind.Serialization.Ssz.SszContainerAttribute;
            [Codec]
            public partial struct Container { public ulong Value { get; set; } }
            """;

        AssertGeneratedEncodingLength(source, nameof(Aliased_container_attribute_generates_a_codec), 8);
    }
}
