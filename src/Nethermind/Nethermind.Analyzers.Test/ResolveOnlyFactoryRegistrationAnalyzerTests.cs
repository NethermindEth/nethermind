// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using NUnit.Framework;

namespace Nethermind.Analyzers.Test;

public class ResolveOnlyFactoryRegistrationAnalyzerTests
{
    private const string Stubs = """
        namespace Autofac
        {
            public interface IComponentContext { }
            public class ContainerBuilder { }
            public class Parameter { }
            public static class ResolutionExtensions
            {
                public static T Resolve<T>(this IComponentContext context) where T : notnull => default!;
                public static T Resolve<T>(this IComponentContext context, params Parameter[] parameters) where T : notnull => default!;
                public static T ResolveKeyed<T>(this IComponentContext context, object key) where T : notnull => default!;
            }
        }
        namespace Autofac.Features.AttributeFilters
        {
            public abstract class ParameterFilterAttribute : System.Attribute { }
            public sealed class KeyFilterAttribute(object key) : ParameterFilterAttribute { }
        }
        namespace Nethermind.Core
        {
            using System;
            using Autofac;
            public static class ContainerBuilderExtensions
            {
                public static ContainerBuilder Add<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) where T : class => builder;
                public static ContainerBuilder AddSingleton<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) where T : class => builder;
                public static ContainerBuilder AddScoped<T>(this ContainerBuilder builder, Func<IComponentContext, T> factory) where T : class => builder;
                public static ContainerBuilder AddKeyedSingleton<T>(this ContainerBuilder builder, string key, Func<IComponentContext, T> factory) where T : class => builder;
                public static ContainerBuilder AddDecorator<T>(this ContainerBuilder builder, Func<IComponentContext, T, T> decorator) where T : class => builder;
            }
        }
        """;

    [TestCase("Add", "ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>())")]
    [TestCase("AddSingleton", "ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>())")]
    [TestCase("AddScoped", "ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>())")]
    [TestCase("AddSingleton", "static ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>())")]
    [TestCase("AddSingleton", "ctx => new Foo(dep2: ctx.Resolve<IDep2>(), dep1: ctx.Resolve<IDep1>())")]
    [TestCase("AddSingleton", "ctx => { return new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>()); }")]
    [TestCase("AddSingleton", "(IComponentContext ctx) => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>())")]
    public Task Factory_that_only_resolves_constructor_arguments_reports(string method, string factory) =>
        Verify(Register(method, $"{{|#0:{factory}|}}"), Diagnostic().WithLocation(0).WithArguments("Foo", method));

    [Test]
    public Task Factory_creating_the_service_type_itself_reports() =>
        Verify(Register("AddSingleton<Foo>", "{|#0:ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>())|}"),
            Diagnostic().WithLocation(0).WithArguments("Foo", "AddSingleton"));

    [Test]
    public Task Factory_creating_generic_type_reports_type_arguments() =>
        Verify(Register("AddSingleton", "{|#0:ctx => new Generic<int>(ctx.Resolve<IDep1>())|}"),
            Diagnostic().WithLocation(0).WithArguments("Generic<int>", "AddSingleton"));

    [TestCase("ctx => { IDep1 dep1 = ctx.Resolve<IDep1>(); return new Foo(dep1, ctx.Resolve<IDep2>()); }")]
    [TestCase("ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>()) { Name = \"x\" }")]
    [TestCase("ctx => new Foo(ctx.Resolve<IDep1>(), new Dep2())")]
    [TestCase("ctx => new Foo(ctx.Resolve<IDep1>(), ctx.ResolveKeyed<IDep2>(\"k\"))")]
    [TestCase("ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>(new Parameter()))")]
    [TestCase("ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<Dep2>())")]
    [TestCase("ctx => new Foo(ctx.Resolve<IDep1>(), Other.Resolve<IDep2>())")]
    [TestCase("ctx => new NoArgs()")]
    [TestCase("ctx => new WithOptional(ctx.Resolve<IDep1>())")]
    [TestCase("ctx => new TwoConstructors(ctx.Resolve<IDep1>())")]
    [TestCase("ctx => new Keyed(ctx.Resolve<IDep1>())")]
    [TestCase("ctx => new InternalConstructor(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>())")]
    [TestCase("ctx => Create(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>())")]
    public Task Factory_with_more_than_constructor_resolution_no_diagnostic(string factory) =>
        Verify(Register("AddSingleton", factory));

    [Test]
    public Task Keyed_registration_without_type_overload_no_diagnostic() =>
        Verify(Wrap("""builder.AddKeyedSingleton<IFoo>("k", ctx => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>()));"""));

    [Test]
    public Task Decorator_no_diagnostic() =>
        Verify(Wrap("builder.AddDecorator<IFoo>((ctx, inner) => new Foo(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>()));"));

    [Test]
    public Task Factory_calling_private_constructor_no_diagnostic() =>
        Verify(Wrap(string.Empty, """
            public class PrivateConstructor : IFoo
            {
                public PrivateConstructor(IDep1 dep1) { }
                private PrivateConstructor(IDep1 dep1, IDep2 dep2) { }
                public static void Load(ContainerBuilder builder) =>
                    builder.AddSingleton<IFoo>(ctx => new PrivateConstructor(ctx.Resolve<IDep1>(), ctx.Resolve<IDep2>()));
            }
            """));

    private static string Register(string method, string factory) =>
        Wrap(method.Contains('<') ? $"builder.{method}({factory});" : $"builder.{method}<IFoo>({factory});");

    private static string Wrap(string statement, string extraTypes = "") => $$"""
        {{Stubs}}
        namespace App
        {
            using Autofac;
            using Autofac.Features.AttributeFilters;
            using Nethermind.Core;
            public interface IDep1 { }
            public interface IDep2 { }
            public class Dep2 : IDep2 { }
            public interface IFoo { }
            public class Foo(IDep1 dep1, IDep2 dep2) : IFoo { public string? Name { get; set; } }
            public class NoArgs : IFoo { }
            public class WithOptional(IDep1 dep1, IDep2? dep2 = null) : IFoo { }
            public class TwoConstructors : IFoo
            {
                public TwoConstructors(IDep1 dep1) { }
                public TwoConstructors(IDep1 dep1, IDep2 dep2) { }
            }
            public class Keyed([KeyFilter("k")] IDep1 dep1) : IFoo { }
            public class Generic<T>(IDep1 dep1) : IFoo { }
            public class InternalConstructor : IFoo
            {
                public InternalConstructor(IDep1 dep1) { }
                internal InternalConstructor(IDep1 dep1, IDep2 dep2) { }
            }
            {{extraTypes}}
            public static class Other
            {
                public static IComponentContext Context = null!;
                public static T Resolve<T>() where T : notnull => Context.Resolve<T>();
            }
            public static class Module
            {
                private static IFoo Create(IDep1 dep1, IDep2 dep2) => new Foo(dep1, dep2);
                public static void Load(ContainerBuilder builder)
                {
                    {{statement}}
                }
            }
        }
        """;

    private static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<ResolveOnlyFactoryRegistrationAnalyzer, DefaultVerifier>.Diagnostic(ResolveOnlyFactoryRegistrationAnalyzer.DiagnosticId);

    private static async Task Verify(string source, params DiagnosticResult[] expected)
    {
        CSharpAnalyzerTest<ResolveOnlyFactoryRegistrationAnalyzer, DefaultVerifier> test = new()
        {
            TestCode = "#nullable enable\n" + source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }
}
