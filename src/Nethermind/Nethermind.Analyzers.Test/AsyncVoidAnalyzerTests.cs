// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class AsyncVoidAnalyzerTests
{
    [TestCase("async void {|#0:M|}() => await Task.Yield();")]
    [TestCase("static async void {|#0:M|}(int x) { await Task.Delay(x); }")]
    [TestCase("private async void {|#0:M|}(object sender) => await Task.Yield();")]
    [TestCase("async void {|#0:M|}(object sender, string e) => await Task.Yield();")]
    [TestCase("async void {|#0:M|}(string sender, EventArgs e) => await Task.Yield();")]
    [TestCase("async void {|#0:M|}<T>(object sender, T e) => await Task.Yield();")]
    public Task Async_void_method_reports_diagnostic(string member) =>
        Verify(WrapMember(member), Diagnostic().WithLocation(0).WithArguments("M"));

    [Test]
    public Task Async_void_local_function_reports_diagnostic() =>
        Verify(WrapMember("""
            void Outer()
            {
                async void {|#0:Local|}() => await Task.Yield();
                Local();
            }
            """), Diagnostic().WithLocation(0).WithArguments("Local"));

    [Test]
    public Task Async_void_override_reports_diagnostic() =>
        Verify("""
            using System.Threading.Tasks;
            abstract class Base
            {
                public abstract void M();
            }
            class Test : Base
            {
                public override async void {|#0:M|}() => await Task.Yield();
            }
            """, Diagnostic().WithLocation(0).WithArguments("M"));

    [TestCase("async void OnChanged(object sender, EventArgs e) => await Task.Yield();")]
    [TestCase("async void OnChanged(object? sender, EventArgs e) => await Task.Yield();")]
    [TestCase("async void OnChanged(object sender, CustomEventArgs e) => await Task.Yield();")]
    [TestCase("async void OnChanged(object sender, DerivedEventArgs e) => await Task.Yield();")]
    [TestCase("async void OnChanged<T>(object sender, T e) where T : EventArgs => await Task.Yield();")]
    [TestCase("async void OnChanged<T>(object sender, T e) where T : CustomEventArgs => await Task.Yield();")]
    public Task Event_handler_signature_no_diagnostic(string member) =>
        Verify(WrapMember(member));

    [TestCase("async Task M() => await Task.Yield();")]
    [TestCase("async ValueTask M() => await Task.Yield();")]
    [TestCase("async Task<int> M() { await Task.Yield(); return 1; }")]
    [TestCase("void M() { }")]
    [TestCase("void M() { Func<Task> f = async () => await Task.Yield(); }")]
    public Task Non_async_void_no_diagnostic(string member) =>
        Verify(WrapMember(member));

    [Test]
    public Task Async_task_local_function_no_diagnostic() =>
        Verify(WrapMember("""
            async Task Outer()
            {
                async Task Local() => await Task.Yield();
                await Local();
            }
            """));

    private static string WrapMember(string member) => $$"""
        #nullable enable
        using System;
        using System.Threading.Tasks;
        class CustomEventArgs : EventArgs { }
        class DerivedEventArgs : CustomEventArgs { }
        class Test
        {
            {{member}}
        }
        """;

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<AsyncVoidAnalyzer, DefaultVerifier>.Diagnostic(AsyncVoidAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<AsyncVoidAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
