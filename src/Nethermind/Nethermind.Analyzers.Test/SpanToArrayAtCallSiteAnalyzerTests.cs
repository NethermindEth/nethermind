// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<SpanToArrayAtCallSiteAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
