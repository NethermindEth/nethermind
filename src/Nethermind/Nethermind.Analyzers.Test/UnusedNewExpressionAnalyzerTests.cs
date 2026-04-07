// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class UnusedNewExpressionAnalyzerTests
{
    private const string AttributeSource = """
        namespace Nethermind.Core.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Constructor)]
            public sealed class ConstructorWithSideEffectAttribute : System.Attribute { }
        }
        """;

    [Test]
    public async Task Unused_new_expression_reports_diagnostic()
    {
        string source = $$"""
            class Foo { }
            class Test
            {
                void M()
                {
                    Foo {|#0:f = new()|};
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments("f", "Foo"));
    }

    [Test]
    public async Task Variable_read_after_assignment_no_diagnostic()
    {
        string source = """
            class Foo { }
            class Test
            {
                void M()
                {
                    Foo f = new();
                    System.Console.WriteLine(f);
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Discard_variable_no_diagnostic()
    {
        string source = """
            class Foo { }
            class Test
            {
                void M()
                {
                    _ = new Foo();
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task ConstructorWithSideEffect_attribute_suppresses_diagnostic()
    {
        string source = $$"""
            {{AttributeSource}}
            class Foo
            {
                [Nethermind.Core.Attributes.ConstructorWithSideEffect]
                public Foo() { }
            }
            class Test
            {
                void M()
                {
                    Foo f = new();
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Statement_new_without_variable_no_diagnostic()
    {
        string source = """
            class Foo { }
            class Test
            {
                void M()
                {
                    new Foo();
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Using_discard_no_diagnostic()
    {
        string source = """
            class Foo : System.IDisposable
            {
                public void Dispose() { }
            }
            class Test
            {
                void M()
                {
                    using var _ = new Foo();
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Explicit_constructor_call_reports_diagnostic()
    {
        string source = $$"""
            class Foo { public Foo(int x) { } }
            class Test
            {
                void M()
                {
                    Foo {|#0:f = new Foo(42)|};
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments("f", "Foo"));
    }

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<UnusedNewExpressionAnalyzer, DefaultVerifier>.Diagnostic(UnusedNewExpressionAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<UnusedNewExpressionAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
