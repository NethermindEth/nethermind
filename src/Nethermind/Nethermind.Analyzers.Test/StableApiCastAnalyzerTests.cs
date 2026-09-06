// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class StableApiCastAnalyzerTests
{
    private const string AttributeAndTypesSource = """
        namespace Nethermind.Core.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Interface)]
            public sealed class StableApiAttribute : System.Attribute { }
        }
        [Nethermind.Core.Attributes.StableApi]
        interface IStable { }
        interface IUnmarked { }
        """;

    // Each snippet marks the offending type syntax with {|#0:...|}; the analyzer reports at the type.
    [TestCase("object x = ({|#0:IStable|})o;", TestName = "Cast")]
    [TestCase("object x = o as {|#0:IStable|};", TestName = "As cast")]
    [TestCase("bool b = o is {|#0:IStable|};", TestName = "Is type check")]
    [TestCase("if (o is {|#0:IStable|} v) { _ = v; }", TestName = "Is declaration pattern")]
    [TestCase("bool b = o is not {|#0:IStable|};", TestName = "Is-not type pattern")]
    [TestCase("switch (o) { case {|#0:IStable|}: break; }", TestName = "Switch case type pattern")]
    [TestCase("int s = o switch { {|#0:IStable|} => 1, _ => 0 };", TestName = "Switch expression type pattern")]
    public async Task Cast_or_type_check_of_stable_interface_reports_diagnostic(string snippet)
    {
        string source = $$"""
            {{AttributeAndTypesSource}}
            class Test
            {
                void M(object o)
                {
                    {{snippet}}
                }
            }
            """;

        await Verify(source, Diagnostic().WithLocation(0).WithArguments("IStable"));
    }

    [TestCase("var t = typeof(IStable);", TestName = "typeof")]
    [TestCase("bool b = typeof(IStable).IsAssignableFrom(o.GetType());", TestName = "IsAssignableFrom")]
    [TestCase("var f = System.Linq.Enumerable.OfType<IStable>(new object[0]);", TestName = "OfType")]
    [TestCase("object x = (IUnmarked)o;", TestName = "Unmarked interface cast")]
    public async Task Reflection_discovery_and_unmarked_interfaces_no_diagnostic(string snippet)
    {
        string source = $$"""
            {{AttributeAndTypesSource}}
            class Test
            {
                void M(object o)
                {
                    {{snippet}}
                }
            }
            """;

        await Verify(source);
    }

    [Test]
    public async Task Test_assembly_is_exempt()
    {
        string source = $$"""
            {{AttributeAndTypesSource}}
            class Test
            {
                void M(object o)
                {
                    object x = (IStable)o;
                }
            }
            """;

        CSharpAnalyzerTest<StableApiCastAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.SolutionTransforms.Add((solution, projectId) => solution.WithProjectAssemblyName(projectId, "Some.Test"));
        await test.RunAsync();
    }

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<StableApiCastAnalyzer, DefaultVerifier>.Diagnostic(StableApiCastAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<StableApiCastAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
