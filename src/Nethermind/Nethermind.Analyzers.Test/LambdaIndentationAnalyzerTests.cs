// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class LambdaIndentationAnalyzerTests
{
    // MaxIndentOffset = 24: body column - statement column must exceed 24 to trigger.
    // Tests run at column 0 base, so a statement at col 4 needs body at col 29+ to fire.

    [Test]
    public async Task Deep_aligned_lambda_body_reports_diagnostic()
    {
        // The `{` opening the lambda block is at column 33 (0-based), statement starts at col 4.
        // Offset = 29 > 24 → fires.
        string source = """
            using System.Linq;
            class C
            {
                void M(int[] data)
                {
                    _ = data.Select(x =>
                                             {|#0:{|}
                                                 return x * 2;
                                             });
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments("33", "25", "4"));
    }

    [TestCase(
        // Normal indent: body `{` sits one extra level past the statement
        """
        using System.Linq;
        class C
        {
            void M(int[] data)
            {
                _ = data.Select(x =>
                {
                    return x * 2;
                });
            }
        }
        """,
        TestName = "Normal_indented_lambda_no_diagnostic")]
    [TestCase(
        // Single-line lambda: arrow and body on the same line — never fires
        """
        using System.Linq;
        class C
        {
            void M(int[] data)
            {
                _ = data.Select(x => x * 2);
            }
        }
        """,
        TestName = "Single_line_lambda_no_diagnostic")]
    [TestCase(
        // Expression body (not block), multi-line but normal depth
        """
        using System.Linq;
        class C
        {
            void M(int[] data)
            {
                _ = data.Select(x =>
                    x * 2);
            }
        }
        """,
        TestName = "Expression_body_normal_depth_no_diagnostic")]
    public async Task No_diagnostic(string source) => await Verify(source);

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<LambdaIndentationAnalyzer, DefaultVerifier>
            .Diagnostic(LambdaIndentationAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<LambdaIndentationAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
