// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class EmptyCatchBlockAnalyzerTests
{
    [TestCase("try { Work(); } {|#0:catch|} { }", "all exceptions")]
    [TestCase("try { Work(); } {|#0:catch|} (Exception) { }", "Exception")]
    [TestCase("try { Work(); } {|#0:catch|} (Exception e) { }", "Exception")]
    [TestCase("try { Work(); } {|#0:catch|} (InvalidOperationException) { }", "InvalidOperationException")]
    [TestCase("try { Work(); Work(); } {|#0:catch|} (ObjectDisposedException) { }", "ObjectDisposedException")]
    [TestCase("try { } {|#0:catch|} (ObjectDisposedException) { }", "ObjectDisposedException")]
    [TestCase("try { if (Flag) Work(); else Work(); } {|#0:catch|} (ObjectDisposedException) { }", "ObjectDisposedException")]
    [TestCase("try { { Work(); Work(); } } {|#0:catch|} (ObjectDisposedException) { }", "ObjectDisposedException")]
    [TestCase("try { Work(); } {|#0:catch|} (AggregateException) { }", "AggregateException")]
    [TestCase("try { Work(); } {|#0:catch|} (Exception e) when (e is OperationCanceledException or InvalidOperationException) { }", "Exception")]
    [TestCase("try { Work(); } {|#0:catch|} (Exception e) when (e is not OperationCanceledException) { }", "Exception")]
    [TestCase("try { Work(); } {|#0:catch|} (Exception e) when (e is OperationCanceledException && Flag) { }", "Exception")]
    [TestCase("try { Work(); Work(); } {|#0:catch|} (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }", "Exception")]
    [TestCase("Exception other = null; try { Work(); } {|#0:catch|} (Exception e) when (other is OperationCanceledException) { }", "Exception")]
    [TestCase("Exception other = null; try { Work(); } {|#0:catch|} (Exception e) when (other is OperationCanceledException or ChannelClosedException) { }", "Exception")]
    [TestCase("try { Work(); } {|#0:catch|} (InvalidOperationException) { /* ignored */ }", "InvalidOperationException")]
    [TestCase("try { Work(); } {|#0:catch|} (CustomException) { }", "CustomException")]
    public Task Empty_catch_reports_diagnostic(string body, string caught) =>
        Verify(WrapBody(body), Diagnostic().WithLocation(0).WithArguments(caught));

    [Test]
    public Task Comment_only_catch_reports_diagnostic() =>
        Verify(WrapBody("""
            try
            {
                Work();
            }
            {|#0:catch|} (InvalidOperationException)
            {
                // ignored
            }
            """), Diagnostic().WithLocation(0).WithArguments("InvalidOperationException"));

    [Test]
    public Task Each_empty_catch_reports_diagnostic() =>
        Verify(WrapBody("""
            try { Work(); }
            {|#0:catch|} (InvalidOperationException) { }
            catch (ArgumentException e) { Console.WriteLine(e); }
            {|#1:catch|} { }
            """),
            Diagnostic().WithLocation(0).WithArguments("InvalidOperationException"),
            Diagnostic().WithLocation(1).WithArguments("all exceptions"));

    [Test]
    public Task Empty_catch_in_lambda_reports_diagnostic() =>
        Verify(WrapBody("""
            Action a = () =>
            {
                try { Work(); } {|#0:catch|} (InvalidOperationException) { }
            };
            a();
            """), Diagnostic().WithLocation(0).WithArguments("InvalidOperationException"));

    [TestCase("try { Work(); } catch (OperationCanceledException) { }")]
    [TestCase("try { Work(); } catch (OperationCanceledException e) { }")]
    [TestCase("try { Work(); } catch (TaskCanceledException) { }")]
    [TestCase("try { Work(); } catch (CustomCanceledException) { }")]
    [TestCase("try { Work(); } catch (OperationCanceledException) when (Flag) { }")]
    [TestCase("try { Work(); } catch (ObjectDisposedException) { }")]
    [TestCase("try { Work(); } catch (ObjectDisposedException e) when (Flag) { }")]
    [TestCase("try { Work(); } catch (ChannelClosedException) { }")]
    [TestCase("try { Work(); } catch (CustomChannelClosedException) { }")]
    [TestCase("try { Work(); Work(); } catch (ChannelClosedException) { }")]
    [TestCase("try { Work(); } catch (Exception e) when (e is OperationCanceledException) { }")]
    [TestCase("try { Work(); } catch (Exception e) when (e is OperationCanceledException or ChannelClosedException) { }")]
    [TestCase("try { Work(); } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }")]
    [TestCase("try { Work(); } catch (Exception e) when (e is TaskCanceledException or CustomChannelClosedException) { }")]
    public Task Expected_shutdown_exception_catch_no_diagnostic(string body) =>
        Verify(WrapBody(body));

    [TestCase("try { Work(); } catch (Exception e) { Console.WriteLine(e); }")]
    [TestCase("try { Work(); } catch { throw; }")]
    [TestCase("try { Work(); } catch (InvalidOperationException) { return; }")]
    [TestCase("try { Work(); } catch (InvalidOperationException) { Flag = true; }")]
    [TestCase("try { Work(); } finally { }")]
    [TestCase("try { Work(); } catch (InvalidOperationException) { { Flag = true; } }")]
    public Task Non_empty_catch_no_diagnostic(string body) =>
        Verify(WrapBody(body));

    private static string WrapBody(string body) => $$"""
        using System;
        using System.Threading.Channels;
        using System.Threading.Tasks;
        class CustomException : Exception { }
        class CustomCanceledException : OperationCanceledException { }
        class CustomChannelClosedException : ChannelClosedException { }
        class Test
        {
            bool Flag;
            void Work() { }
            void M()
            {
                {{body}}
            }
        }
        """;

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<EmptyCatchBlockAnalyzer, DefaultVerifier>.Diagnostic(EmptyCatchBlockAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<EmptyCatchBlockAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
