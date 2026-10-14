// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Nethermind.Serialization.SszGenerator.Test;

public partial class SszGeneratorDiagnosticTest
{
    [Test]
    public void Nested_unions_accept_different_selector_sets([Values] bool differentOptionCounts)
    {
        string source = WithOuterUnion($$"""
            public enum LeftSelector : byte { Value = 1 }
            [SszCompatibleUnion]
            public partial struct Left
            {
                public LeftSelector Selector { get; set; }
                public ulong Value { get; set; }
            }
            public enum RightSelector : byte { Value = 2{{(differentOptionCounts ? ", Other = 3" : string.Empty)}} }
            [SszCompatibleUnion]
            public partial struct Right
            {
                public RightSelector Selector { get; set; }
                public ulong Value { get; set; }
                {{(differentOptionCounts ? "public ulong Other { get; set; }" : string.Empty)}}
            }
            """);

        GeneratorDriverRunResult result = RunGenerator(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Nested_unions_accept_different_selector_sets), out Compilation output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(output.GetDiagnostics(), Has.None.Matches<Diagnostic>(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    [Test]
    public void Nested_unions_reject_incompatible_cross_pairs()
    {
        string source = WithOuterUnion("""
            [SszContainer]
            public partial struct A { [SszField(0)] public ulong X { get; set; } }
            [SszContainer]
            public partial struct B { [SszField(1)] public byte Y { get; set; } }
            [SszContainer]
            public partial struct C { [SszField(0)] public bool X { get; set; } }
            public enum LeftSelector : byte { First = 1, Second = 2 }
            [SszCompatibleUnion]
            public partial struct Left
            {
                public LeftSelector Selector { get; set; }
                public A First { get; set; }
                public B Second { get; set; }
            }
            public enum RightSelector : byte { First = 1, Second = 2 }
            [SszCompatibleUnion]
            public partial struct Right
            {
                public RightSelector Selector { get; set; }
                public B First { get; set; }
                public C Second { get; set; }
            }
            """);

        Diagnostic diagnostic = GetSsz003Diagnostic(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Nested_unions_reject_incompatible_cross_pairs));

        Assert.That(diagnostic.GetMessage(), Does.Contain("Compatible union Outer must contain pairwise-compatible member types"));
    }

    private static string WithOuterUnion(string declarations) => "using Nethermind.Serialization.Ssz;\n" + declarations + """

        public enum OuterSelector : byte { Left = 1, Right = 2 }
        [SszCompatibleUnion]
        public partial struct Outer
        {
            public OuterSelector Selector { get; set; }
            public Left Left { get; set; }
            public Right Right { get; set; }
        }
        """;
}
