// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class SensitiveLogMarkerAnalyzerTests
{
    private const string TestPrelude = """
        using System.Runtime.CompilerServices;
        using Nethermind.Logging;

        namespace Nethermind.Logging
        {
            [InterpolatedStringHandler]
            public ref struct InfoInterpolatedStringHandler
            {
                public InfoInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
                {
                    shouldAppend = true;
                }

                public void AppendLiteral(string value) { }
                public void AppendFormatted<T>(T value) { }
                public void AppendFormatted<T>(T value, string format) { }
                public void AppendFormatted<T>(T value, int alignment, string format) { }
            }

            [InterpolatedStringHandler]
            public ref struct DebugInterpolatedStringHandler
            {
                public DebugInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
                {
                    shouldAppend = true;
                }

                public void AppendLiteral(string value) { }
                public void AppendFormatted<T>(T value, string format) { }
            }

            [InterpolatedStringHandler]
            public ref struct TraceInterpolatedStringHandler
            {
                public TraceInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
                {
                    shouldAppend = true;
                }

                public void AppendLiteral(string value) { }
                public void AppendFormatted<T>(T value, string format) { }
            }

            [InterpolatedStringHandler]
            public ref struct WarnInterpolatedStringHandler
            {
                public WarnInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
                {
                    shouldAppend = true;
                }

                public void AppendLiteral(string value) { }
                public void AppendFormatted<T>(T value, string format) { }
            }

            [InterpolatedStringHandler]
            public ref struct ErrorInterpolatedStringHandler
            {
                public ErrorInterpolatedStringHandler(int literalLength, int formattedCount, ILogger logger, out bool shouldAppend)
                {
                    shouldAppend = true;
                }

                public void AppendLiteral(string value) { }
                public void AppendFormatted<T>(T value, string format) { }
            }

            public struct ILogger
            {
                public void Info(string text) { }
                public void Info([InterpolatedStringHandlerArgument("")] ref InfoInterpolatedStringHandler handler) { }
                public void Debug(string text) { }
                public void Debug([InterpolatedStringHandlerArgument("")] ref DebugInterpolatedStringHandler handler) { }
                public void Trace(string text) { }
                public void Trace([InterpolatedStringHandlerArgument("")] ref TraceInterpolatedStringHandler handler) { }
                public void Warn(string text) { }
                public void Warn([InterpolatedStringHandlerArgument("")] ref WarnInterpolatedStringHandler handler) { }
                public void Error(string text) { }
                public void Error([InterpolatedStringHandlerArgument("")] ref ErrorInterpolatedStringHandler handler) { }
                public void DebugError([InterpolatedStringHandlerArgument("")] ref DebugInterpolatedStringHandler handler) { }
                public void TraceWarn([InterpolatedStringHandlerArgument("")] ref TraceInterpolatedStringHandler handler) { }
                public void Other(string text) { }
            }
        }

        """;

    [Test]
    public async Task Direct_log_interpolation_accepts_hide_marker()
    {
        string source = TestPrelude + """
            class C
            {
                static void Use(ILogger logger, string endpoint)
                {
                    logger.Info($"Node {endpoint:hide}");
                    logger.Info($"Node {endpoint,24:hide}");
                    logger.Info($"First {endpoint:hide}" + $" second {endpoint:hide}");
                    logger.Info($"Port {42:D5}");
                    logger.Debug($"Node {endpoint:hide}");
                    logger.Trace($"Node {endpoint:hide}");
                    logger.Warn($"Node {endpoint:hide}");
                    logger.Error($"Node {endpoint:hide}");
                    logger.DebugError($"Node {endpoint:hide}");
                    logger.TraceWarn($"Node {endpoint:hide}");
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Marker_outside_handler_reports_compilation_errors()
    {
        string source = TestPrelude + """
            class C
            {
                static void Use(ILogger logger, string endpoint, bool added)
                {
                    logger.Info(added ? $"Added {endpoint:{|#0:hide|}}" : "already added");
                    string message = $"Node {endpoint:{|#1:hide|}}";
                    logger.Info(message);
                    logger.Other($"Node {endpoint:{|#2:hide|}}");
                    logger.Info($"Outer {$"Inner {endpoint:{|#3:hide|}}"}");
                    logger.Info($"Node {endpoint:{|#4:Hide|}}");
                    logger.Info($"Node {endpoint:{|#5:sensitive|}}");
                    logger.Info($"Node {endpoint:{|#6:Sensitive|}}");
                    logger.Info($"Node {endpoint:{|#7:hide|}}" + " suffix");
                    System.Guid peer = default;
                    logger.Other($"Peer {peer:{|#8:hide|}}");
                    logger.Info(($"Node {endpoint:{|#9:hide|}}"));
                }
            }
            """;

        await Verify(source,
            Diagnostic().WithLocation(0).WithArguments("hide"),
            Diagnostic().WithLocation(1).WithArguments("hide"),
            Diagnostic().WithLocation(2).WithArguments("hide"),
            Diagnostic().WithLocation(3).WithArguments("hide"),
            Diagnostic().WithLocation(4).WithArguments("Hide"),
            Diagnostic().WithLocation(5).WithArguments("sensitive"),
            Diagnostic().WithLocation(6).WithArguments("Sensitive"),
            Diagnostic().WithLocation(7).WithArguments("hide"),
            Diagnostic().WithLocation(8).WithArguments("hide"),
            Diagnostic().WithLocation(9).WithArguments("hide"));
    }

    [Test]
    public async Task Generated_source_reports_marker_misuse()
    {
        string source = "// <auto-generated/>\n" + TestPrelude + """
            class C
            {
                static void Use(ILogger logger, string endpoint) =>
                    logger.Other($"Node {endpoint:{|#0:hide|}}");
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments("hide"));
    }

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<SensitiveLogMarkerAnalyzer, DefaultVerifier>
            .Diagnostic(SensitiveLogMarkerAnalyzer.DiagnosticId)
            .WithSeverity(DiagnosticSeverity.Error);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<SensitiveLogMarkerAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
