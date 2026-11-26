// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class SpanToArrayAtCallSiteAnalyzerTests
{
    [Test]
    public async Task Span_to_array_with_Span_overload_reports()
    {
        string source = """
            using System;
            class C
            {
                static void M(byte[] x) { }
                static void M(Span<byte> x) { }
                static void Use(Span<byte> s) => M({|#0:s.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("M", "Span", "byte"));
    }

    [Test]
    public async Task ReadOnlySpan_to_array_with_ReadOnlySpan_overload_reports()
    {
        string source = """
            using System;
            class C
            {
                static void M(byte[] x) { }
                static void M(ReadOnlySpan<byte> x) { }
                static void Use(ReadOnlySpan<byte> s) => M({|#0:s.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("M", "ReadOnlySpan", "byte"));
    }

    [Test]
    public async Task Span_to_array_with_only_ReadOnlySpan_overload_reports()
    {
        string source = """
            using System;
            class C
            {
                static void M(byte[] x) { }
                static void M(ReadOnlySpan<byte> x) { }
                static void Use(Span<byte> s) => M({|#0:s.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("M", "ReadOnlySpan", "byte"));
    }

    [Test]
    public async Task ReadOnlySpan_to_array_with_only_Span_overload_no_diagnostic()
    {
        string source = """
            using System;
            class C
            {
                static void M(byte[] x) { }
                static void M(Span<byte> x) { }
                static void Use(ReadOnlySpan<byte> s) => M(s.ToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task No_span_overload_no_diagnostic()
    {
        string source = """
            using System;
            class C
            {
                static void M(byte[] x) { }
                static void Use(Span<byte> s) => M(s.ToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task ToArray_on_List_no_diagnostic()
    {
        string source = """
            using System;
            using System.Collections.Generic;
            class C
            {
                static void M(byte[] x) { }
                static void M(Span<byte> x) { }
                static void Use(List<byte> list) => M(list.ToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Other_parameter_differs_no_diagnostic()
    {
        string source = """
            using System;
            class C
            {
                static void M(byte[] x, int y) { }
                static void M(Span<byte> x, string y) { }
                static void Use(Span<byte> s) => M(s.ToArray(), 1);
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Params_array_no_diagnostic()
    {
        string source = """
            using System;
            class C
            {
                static void M(params byte[] x) { }
                static void M(Span<byte> x) { }
                static void Use(Span<byte> s) => M(s.ToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Constructor_with_span_overload_reports()
    {
        string source = """
            using System;
            class Box
            {
                public Box(byte[] x) { }
                public Box(ReadOnlySpan<byte> x) { }
            }
            class C
            {
                static Box Use(ReadOnlySpan<byte> s) => new Box({|#0:s.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments(".ctor", "ReadOnlySpan", "byte"));
    }

    [Test]
    public async Task Generic_method_with_span_overload_reports()
    {
        string source = """
            using System;
            class C
            {
                static void M<T>(T[] x) { }
                static void M<T>(ReadOnlySpan<T> x) { }
                static void Use(ReadOnlySpan<int> s) => M({|#0:s.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("M", "ReadOnlySpan", "int"));
    }

    private static IEnumerable<TestCaseData> ConstrainedSpanOverloadCases()
    {
        yield return new TestCaseData("unmanaged", "int", true);
        yield return new TestCaseData("unmanaged", "string", false); // CS8377
        yield return new TestCaseData("class", "string", true);
        yield return new TestCaseData("class", "int", false); // CS0452
        yield return new TestCaseData("class", "string?", false); // CS8634, an error when warnings are errors
        yield return new TestCaseData("notnull", "string", true);
        yield return new TestCaseData("notnull", "string?", false); // CS8714, an error when warnings are errors
        yield return new TestCaseData("IComparable<T>", "int", true);
        yield return new TestCaseData("IComparable<T>", "object", false); // CS0311
        yield return new TestCaseData("IDisposable?", "IDisposable?", true);
        yield return new TestCaseData("IDisposable", "IDisposable?", false); // CS8631, an error when warnings are errors
    }

    [TestCaseSource(nameof(ConstrainedSpanOverloadCases))]
    public async Task Constrained_generic_span_overload_reports_only_when_call_compiles(string constraint, string typeArgument, bool reports)
    {
        string Arg(int location) => reports ? $"{{|#{location}:s.ToArray()|}}" : "s.ToArray()";
        string source = $$"""
            #nullable enable
            using System;
            class R
            {
                public int Count<T>(T[] values) => values.Length;
                public int Count<T>(ReadOnlySpan<T> values) where T : {{constraint}} => values.Length;
            }
            static class E
            {
                public static int Count<T>(this string receiver, T[] values) => values.Length;
                public static int Count<T>(this string receiver, ReadOnlySpan<T> values) where T : {{constraint}} => values.Length;
            }
            class C
            {
                static int Instance(R r, ReadOnlySpan<{{typeArgument}}> s) => r.Count<{{typeArgument}}>({{Arg(0)}});
                static int? Conditional(R? r, ReadOnlySpan<{{typeArgument}}> s) => r?.Count<{{typeArgument}}>({{Arg(1)}});
                static int Extension(string e, ReadOnlySpan<{{typeArgument}}> s) => e.Count<{{typeArgument}}>({{Arg(2)}});
                static int Static(string e, ReadOnlySpan<{{typeArgument}}> s) => E.Count<{{typeArgument}}>(e, {{Arg(3)}});
                static void ConditionalInferred(string? e, ReadOnlySpan<{{typeArgument}}> s)
                {
                    e?.Count({{Arg(4)}});
                }
            }
            """;
        DiagnosticResult[] expected = reports
            ? [.. Enumerable.Range(0, 5).Select(i => Diagnostic().WithLocation(i).WithArguments("Count", "ReadOnlySpan", typeArgument))]
            : [];
        await Verify(source, expected);
    }

    [TestCase("byte", true)]
    [TestCase("Span<byte>", false)] // CS9244
    public async Task Span_overload_without_allows_ref_struct_reports_only_when_call_compiles(string tag, bool reports)
    {
        string argument = reports ? "{|#0:s.ToArray()|}" : "s.ToArray()";
        string source = $$"""
            using System;
            class C
            {
                static int Count<T, TTag>(T[] values) where TTag : allows ref struct => values.Length;
                static int Count<T, TTag>(ReadOnlySpan<T> values) => values.Length;
                static int Use(ReadOnlySpan<int> s) => Count<int, {{tag}}>({{argument}});
            }
            """;
        DiagnosticResult[] expected = reports ? [Diagnostic().WithLocation(0).WithArguments("Count", "ReadOnlySpan", "int")] : [];
        await Verify(source, ReferenceAssemblies.Net.Net90, expected);
    }

    [Test]
    public async Task Inaccessible_span_overload_no_diagnostic()
    {
        // Helper.M(byte[]) is public; Helper.M(Span<byte>) is private. From outside the class
        // the analyzer must NOT suggest the span overload because applying the fix would not compile.
        string source = """
            using System;
            class Helper
            {
                public static void M(byte[] x) { }
                private static void M(Span<byte> x) { }
            }
            class Caller
            {
                static void Use(Span<byte> s) => Helper.M(s.ToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Method_level_span_overload_chaining_to_array_no_diagnostic()
    {
        string source = """
            using System;
            class C
            {
                static void Foo(byte[] x) { }
                static void Foo(ReadOnlySpan<byte> s) => Foo(s.ToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Span_overload_chaining_to_array_overload_no_diagnostic()
    {
        string source = """
            using System;
            class Box
            {
                public byte[] Bytes { get; }
                public Box(byte[] bytes) { Bytes = bytes; }
                public Box(ReadOnlySpan<byte> s) : this(s.ToArray()) { }
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Direct_span_param_with_implicit_array_conversion_reports()
    {
        // No array overload exists — param is already Span<byte>; the array from ToArray()
        // is implicitly converted to Span<byte>. The .ToArray() is pointless.
        string source = """
            using System;
            class C
            {
                static void M(Span<byte> x) { }
                static void Use(Span<byte> s) => M({|#0:s.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("M", "Span", "byte"));
    }

    [Test]
    public async Task Direct_ReadOnlySpan_param_from_Span_caller_reports()
    {
        // M(ReadOnlySpan<byte>) only; Span<byte>.ToArray() -> byte[] -> ReadOnlySpan<byte>.
        string source = """
            using System;
            class C
            {
                static void M(ReadOnlySpan<byte> x) { }
                static void Use(Span<byte> s) => M({|#0:s.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("M", "ReadOnlySpan", "byte"));
    }

    [Test]
    public async Task Direct_ReadOnlySpan_param_from_ReadOnlySpan_caller_reports()
    {
        string source = """
            using System;
            class C
            {
                static void M(ReadOnlySpan<byte> x) { }
                static void Use(ReadOnlySpan<byte> s) => M({|#0:s.ToArray()|});
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("M", "ReadOnlySpan", "byte"));
    }

    [Test]
    public async Task Direct_Span_param_from_ReadOnlySpan_caller_no_diagnostic()
    {
        // ReadOnlySpan<byte> cannot fit into Span<byte> param; ToArray is required.
        string source = """
            using System;
            class C
            {
                static void M(Span<byte> x) { }
                static void Use(ReadOnlySpan<byte> s) => M(s.ToArray());
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Element_type_mismatch_no_diagnostic()
    {
        string source = """
            using System;
            class C
            {
                static void M(byte[] x) { }
                static void M(Span<int> x) { }
                static void Use(Span<byte> s) => M(s.ToArray());
            }
            """;
        await Verify(source);
    }

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<SpanToArrayAtCallSiteAnalyzer, DefaultVerifier>.Diagnostic(SpanToArrayAtCallSiteAnalyzer.DiagnosticId);

    private static Task Verify(string source, params DiagnosticResult[] expected) =>
        Verify(source, ReferenceAssemblies.Net.Net80, expected);

    private static async Task Verify(string source, ReferenceAssemblies referenceAssemblies, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<SpanToArrayAtCallSiteAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = referenceAssemblies,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
