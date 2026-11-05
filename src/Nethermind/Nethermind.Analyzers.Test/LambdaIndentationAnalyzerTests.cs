// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class LambdaIndentationAnalyzerTests
{
    // MaxIndentOffset = 4: body column - arrow-line first-token column must exceed 4 to trigger.

    [Test]
    public async Task Deep_aligned_lambda_body_reports_diagnostic()
    {
        // The `{` opening the lambda block is at column 33 (0-based), arrow line starts at col 8.
        // Offset = 25 > 4 → fires.
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

    [Test]
    public async Task Arrow_on_own_line_deep_body_reports_diagnostic()
    {
        // Arrow `=>` wraps to its own line at col 12. Body `{` at col 33. Offset = 33 - 12 = 21 > 4 → fires.
        string source = """
            using System.Linq;
            class C
            {
                void M(int[] data)
                {
                    _ = data.Select(x
                        =>
                                             {|#0:{|}
                                                 return x * 2;
                                             });
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments("33", "21", "4"));
    }

    [Test]
    public async Task Nested_lambda_deep_body_reports_diagnostic()
    {
        // Inner lambda body at col 33, arrow line first token `_` at col 8. Offset = 25 > 4 → fires.
        string source = """
            using System.Linq;
            class C
            {
                void M(int[][] data)
                {
                    _ = data.Select(xs => xs.Select(x =>
                                             {|#0:{|}
                                                 return x * 2;
                                             }));
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments("33", "25", "4"));
    }

    [Test]
    public async Task Expression_body_too_deep_reports_diagnostic()
    {
        // Expression body (no block) at col 33, arrow line starts at col 8. Offset = 25 > 4 → fires.
        string source = """
            using System.Linq;
            class C
            {
                void M(int[] data)
                {
                    _ = data.Select(x =>
                                             {|#0:x|} * 2);
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
    [TestCase(
        // Arrow on its own line, body at normal depth
        """
        using System.Linq;
        class C
        {
            void M(int[] data)
            {
                _ = data.Select(x
                    =>
                    {
                        return x * 2;
                    });
            }
        }
        """,
        TestName = "Arrow_on_own_line_normal_depth_no_diagnostic")]
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
