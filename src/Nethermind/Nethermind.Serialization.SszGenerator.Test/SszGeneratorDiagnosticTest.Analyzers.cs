// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Nethermind.Serialization.SszGenerator.Test;

public partial class SszGeneratorDiagnosticTest
{
    [TestCase("[SszContainer] public partial class Container : Parent { } public class Parent { public ulong Value { get; set; } }", null)]
    [TestCase("[SszContainer] public partial class Container { } public partial class Container { public ulong Value { get; set; } }", null)]
    [TestCase("[SszContainer] public partial class Container { public ulong Value { get; protected set; } }", "SSZ001")]
    [TestCase("[SszContainer] public partial record Container { public ulong Value { get; set; } }", null)]
    [TestCase("[SszContainer] public partial class Container { [SszIgnore] public byte[] Value { get; set; } public ulong Number { get; set; } }", null)]
    [TestCase("[SszContainer] public partial class Container { [SszIgnore] public byte[] Value { get; set; } }", "SSZ001")]
    [TestCase("[SszContainer] public partial class Container : Parent { } public class Parent { public byte[] Value { get; set; } }", "SSZ002")]
    [TestCase("[SszContainer] public partial class Container { public Custom Value { get; set; } } public class Custom { public System.ReadOnlySpan<ulong> AsSpan() => default; public Custom(System.ReadOnlySpan<ulong> value) { } }", "SSZ002")]
    [TestCase("[Nethermind.Serialization.Ssz.SszContainer] public partial class Container { [Other.SszIgnore] public byte[] Value { get; set; } } namespace Other { public class SszIgnoreAttribute : System.Attribute { } }", "SSZ002")]
    [TestCase("[Nethermind.Serialization.Ssz.SszContainer] public partial class Container { [Other.SszList] public byte[] Value { get; set; } } namespace Other { public class SszListAttribute : System.Attribute { } }", "SSZ002")]
    [TestCase("[Other.SszContainer] public partial class Container { public byte[] Value { get; set; } } namespace Other { public class SszContainerAttribute : System.Attribute { } }", null)]
    [TestCase("[Codec] public partial class Container { public byte[] Value { get; set; } }", "SSZ002")]
    public async Task Analyzer_uses_the_generators_resolved_members(string declarations, string? expectedDiagnostic)
    {
        string source = "using Nethermind.Serialization.Ssz; using Codec = Nethermind.Serialization.Ssz.SszContainerAttribute;\n" + declarations;
        RunGenerator(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), nameof(Analyzer_uses_the_generators_resolved_members), out Compilation output);

        Type analyzerType = CreateSszGenerator().GetType().Assembly.GetType("SszDiagnosticAnalyzer", throwOnError: true)!;
        DiagnosticAnalyzer analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!;
        System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics = await output
            .WithAnalyzers([analyzer])
            .GetAnalyzerDiagnosticsAsync();

        if (expectedDiagnostic is null)
        {
            Assert.That(diagnostics, Is.Empty);
        }
        else
        {
            Assert.That(diagnostics, Has.Length.EqualTo(1));
            Assert.That(diagnostics[0].Id, Is.EqualTo(expectedDiagnostic));
        }
    }
}
