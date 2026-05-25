// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzerTests
{
    [TestCase("new TaskCompletionSource()")]
    [TestCase("new TaskCompletionSource((object?)null)")]
    [TestCase("new TaskCompletionSource(TaskCreationOptions.None)")]
    [TestCase("new TaskCompletionSource(TaskCreationOptions.LongRunning)")]
    [TestCase("new TaskCompletionSource(creationOptions: TaskCreationOptions.None)")]
    [TestCase("new TaskCompletionSource(opt)")]
    [TestCase("new TaskCompletionSource<int>()")]
    [TestCase("new TaskCompletionSource<int>((object?)null)")]
    [TestCase("new TaskCompletionSource<int>(TaskCreationOptions.None)")]
    [TestCase("new TaskCompletionSource<int>(creationOptions: TaskCreationOptions.None)")]
    public Task Construction_without_run_continuations_async_reports_diagnostic(string expression) =>
        Verify(WrapMethodBody($"var t = {{|#0:{expression}|}};"), Diagnostic().WithLocation(0));

    [TestCase("new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)")]
    [TestCase("new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously | TaskCreationOptions.LongRunning)")]
    [TestCase("new TaskCompletionSource((object?)null, TaskCreationOptions.RunContinuationsAsynchronously)")]
    [TestCase("new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)")]
    [TestCase("new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously | TaskCreationOptions.LongRunning)")]
    public Task Construction_with_run_continuations_async_no_diagnostic(string expression) =>
        Verify(WrapMethodBody($"var t = {expression};"));

    [Test]
    public Task Const_local_with_run_continuations_async_no_diagnostic() =>
        Verify(WrapMethodBody("""
            const TaskCreationOptions opts = TaskCreationOptions.RunContinuationsAsynchronously;
            var t = new TaskCompletionSource(opts);
            """));

    [Test]
    public Task TargetTyped_new_reports_diagnostic() =>
        Verify(WrapMethodBody("TaskCompletionSource<int> t = {|#0:new()|};"), Diagnostic().WithLocation(0));

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

    private static string WrapMethodBody(string body) => $$"""
        using System.Threading.Tasks;
        class Test
        {
            void M(TaskCreationOptions opt)
            {
                {{body}}
            }
        }
        """;

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzer, DefaultVerifier>.Diagnostic(
            TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<TaskCompletionSourceMustRunContinuationsAsynchronouslyAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
