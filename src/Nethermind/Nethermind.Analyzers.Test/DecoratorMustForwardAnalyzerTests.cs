// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class DecoratorMustForwardAnalyzerTests
{
    // Shared prelude declaring the attribute and a tagged interface. Kept inline so the
    // analyzer's lookup of MustForwardOnDecorateAttribute resolves within the test compilation.
    private const string Prelude = """
        namespace Nethermind.Core.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Property)]
            public sealed class MustForwardOnDecorateAttribute : System.Attribute { }
        }

        namespace N
        {
            using Nethermind.Core.Attributes;

            public interface IThing
            {
                [MustForwardOnDecorate]
                void Touch(int x) { }

                // Untagged DIM — must NOT trigger the analyzer.
                void Untagged(int x) { }
            }
        }
        """;

    [Test]
    public async Task Non_decorator_using_DIM_no_diagnostic()
    {
        string source = Prelude + """
            namespace N
            {
                public class Leaf : IThing { }
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Decorator_with_field_using_DIM_reports()
    {
        string source = Prelude + """
            namespace N
            {
                public class {|#0:Decorator|} : IThing
                {
                    private readonly IThing _inner;
                    public Decorator(IThing inner) { _inner = inner; }
                }
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("Decorator", "IThing", "IThing.Touch(int)"));
    }

    [Test]
    public async Task Decorator_with_primary_ctor_param_using_DIM_reports()
    {
        string source = Prelude + """
            namespace N
            {
                public class {|#0:Decorator|}(IThing inner) : IThing { }
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("Decorator", "IThing", "IThing.Touch(int)"));
    }

    [Test]
    public async Task Decorator_with_property_using_DIM_reports()
    {
        string source = Prelude + """
            namespace N
            {
                public class {|#0:Decorator|} : IThing
                {
                    public IThing Inner { get; init; } = null!;
                }
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("Decorator", "IThing", "IThing.Touch(int)"));
    }

    [Test]
    public async Task Decorator_forwarding_via_class_method_no_diagnostic()
    {
        string source = Prelude + """
            namespace N
            {
                public class Decorator(IThing inner) : IThing
                {
                    public void Touch(int x) => inner.Touch(x);
                }
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Decorator_forwarding_via_explicit_interface_implementation_no_diagnostic()
    {
        string source = Prelude + """
            namespace N
            {
                public class Decorator(IThing inner) : IThing
                {
                    void IThing.Touch(int x) => inner.Touch(x);
                }
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Abstract_decorator_using_DIM_reports()
    {
        // The bug lives in the base: a concrete subclass that inherits this scaffold without
        // declaring its own IThing field would not be detected as a decorator. Flag the base.
        string source = Prelude + """
            namespace N
            {
                public abstract class {|#0:BaseDecorator|}(IThing inner) : IThing { }
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("BaseDecorator", "IThing", "IThing.Touch(int)"));
    }

    [Test]
    public async Task Decorator_inheriting_forward_from_base_class_no_diagnostic()
    {
        string source = Prelude + """
            namespace N
            {
                public abstract class BaseDecorator(IThing inner) : IThing
                {
                    public virtual void Touch(int x) => inner.Touch(x);
                }

                public class Derived(IThing inner) : BaseDecorator(inner) { }
            }
            """;
        await Verify(source);
    }

    [Test]
    public async Task Tagged_property_decorator_reports()
    {
        string source = """
            namespace Nethermind.Core.Attributes
            {
                [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Property)]
                public sealed class MustForwardOnDecorateAttribute : System.Attribute { }
            }

            namespace N
            {
                using Nethermind.Core.Attributes;

                public interface IThing
                {
                    [MustForwardOnDecorate]
                    int Counter => 0;
                }

                public class {|#0:Decorator|}(IThing inner) : IThing { }
            }
            """;
        await Verify(source, Diagnostic().WithLocation(0).WithArguments("Decorator", "IThing", "IThing.Counter"));
    }

    [Test]
    public async Task Untagged_member_using_DIM_no_diagnostic()
    {
        // Decorator forwards the tagged member but lets the untagged one fall through to the DIM.
        string source = Prelude + """
            namespace N
            {
                public class Decorator(IThing inner) : IThing
                {
                    public void Touch(int x) => inner.Touch(x);
                }
            }
            """;
        await Verify(source);
    }

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<DecoratorMustForwardAnalyzer, DefaultVerifier>.Diagnostic(DecoratorMustForwardAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<DecoratorMustForwardAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
