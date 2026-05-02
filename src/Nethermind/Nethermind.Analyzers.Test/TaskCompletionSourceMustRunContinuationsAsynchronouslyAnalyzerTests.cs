// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzerTests
{
    [TestCase("new System.Threading.Tasks.TaskCompletionSource()")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource((object?)null)")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.None)")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.LongRunning)")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource<int>()")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.None)")]
    public async Task Construction_without_run_continuations_async_reports_diagnostic(string expression)
    {
        string source = $$"""
            class Test
            {
                void M()
                {
                    var t = {|#0:{{expression}}|};
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0));
    }

    [TestCase("new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously | System.Threading.Tasks.TaskCreationOptions.LongRunning)")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource((object?)null, System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)")]
    [TestCase("new System.Threading.Tasks.TaskCompletionSource<int>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously | System.Threading.Tasks.TaskCreationOptions.LongRunning)")]
    public async Task Construction_with_run_continuations_async_no_diagnostic(string expression)
    {
        string source = $$"""
            class Test
            {
                void M()
                {
                    var t = {{expression}};
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task NonConstant_options_expression_reports_diagnostic()
    {
        string source = """
            class Test
            {
                void M(System.Threading.Tasks.TaskCreationOptions opt)
                {
                    var t = {|#0:new System.Threading.Tasks.TaskCompletionSource(opt)|};
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0));
    }

    [Test]
    public async Task Other_type_named_TaskCompletionSource_no_diagnostic()
    {
        string source = """
            namespace Other
            {
                class TaskCompletionSource { }
            }
            class Test
            {
                void M()
                {
                    var t = new Other.TaskCompletionSource();
                }
            }
            """;

        await Verify(source);
    }

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzer, DefaultVerifier>.Diagnostic(
            TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
