// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Analyzers;

/// <summary>
/// Reports <c>Add</c>/<c>AddSingleton</c>/<c>AddScoped</c> factory registrations from
/// <c>Nethermind.Core.ContainerBuilderExtensions</c> whose lambda only does
/// <c>new TImpl(ctx.Resolve&lt;A&gt;(), ctx.Resolve&lt;B&gt;())</c>, where each argument resolves exactly the
/// constructor parameter type and the factory calls the single public constructor of <c>TImpl</c>. A type
/// registration (<c>Add&lt;T, TImpl&gt;()</c>) builds the same object through that constructor and does not have
/// to be edited whenever the constructor changes.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ResolveOnlyFactoryRegistrationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "NETH011";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Register the type instead of a factory that only resolves its constructor arguments",
        "Factory only resolves the constructor arguments of '{0}', register it with {1}<TService, {0}>() instead",
        category: "Maintainability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A factory such as .Add<IFoo>(ctx => new Foo(ctx.Resolve<A>(), ctx.Resolve<B>())) builds Foo " +
                     "through the same public constructor that a type registration would use, but must be edited " +
                     "whenever Foo gains a dependency. Use .Add<IFoo, Foo>(), .AddSingleton<IFoo, Foo>() or " +
                     ".AddScoped<IFoo, Foo>(). Note that the AddSingleton and AddScoped type overloads also register " +
                     "Foo as itself. Factories that resolve a different type than the parameter, add logic, use an " +
                     "object initializer, call a non-public constructor, or target a type with several public " +
                     "constructors are not reported.");

    private static readonly ImmutableHashSet<string> RegistrationMethods = ["Add", "AddSingleton", "AddScoped"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            INamedTypeSymbol? extensions = startContext.Compilation.GetTypeByMetadataName("Nethermind.Core.ContainerBuilderExtensions");
            INamedTypeSymbol? resolution = startContext.Compilation.GetTypeByMetadataName("Autofac.ResolutionExtensions");
            INamedTypeSymbol? componentContext = startContext.Compilation.GetTypeByMetadataName("Autofac.IComponentContext");
            if (extensions is null || resolution is null || componentContext is null) return;

            startContext.RegisterOperationAction(
                ctx => Analyze(ctx, extensions, resolution, componentContext),
                OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol extensions, INamedTypeSymbol resolution, INamedTypeSymbol componentContext)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        if (!RegistrationMethods.Contains(method.Name)) return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, extensions)) return;

        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            if (argument.Value is not IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda }) continue;
            if (lambda.Symbol.Parameters.Length != 1) continue;
            IParameterSymbol contextParameter = lambda.Symbol.Parameters[0];
            if (!SymbolEqualityComparer.Default.Equals(contextParameter.Type, componentContext)) continue;

            if (ReturnedCreation(lambda.Body) is not { } creation) continue;
            if (!OnlyResolvesConstructorArguments(creation, contextParameter, resolution)) continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, lambda.Syntax.GetLocation(), creation.Type!.Name, method.Name));
        }
    }

    private static IObjectCreationOperation? ReturnedCreation(IBlockOperation body)
    {
        if (body.Operations.Length != 1 || body.Operations[0] is not IReturnOperation { ReturnedValue: { } returned }) return null;

        while (returned is IConversionOperation { IsImplicit: true } conversion)
            returned = conversion.Operand;

        return returned as IObjectCreationOperation;
    }

    private static bool OnlyResolvesConstructorArguments(IObjectCreationOperation creation, IParameterSymbol contextParameter, INamedTypeSymbol resolution)
    {
        if (creation.Initializer is not null || creation.Constructor is not { } constructor) return false;
        if (constructor.DeclaredAccessibility != Accessibility.Public) return false;
        if (creation.Type is not INamedTypeSymbol type || !HasSingleRegistrableConstructor(type)) return false;
        if (constructor.Parameters.IsEmpty || creation.Arguments.Length != constructor.Parameters.Length) return false;

        foreach (IArgumentOperation argument in creation.Arguments)
        {
            if (argument.ArgumentKind != ArgumentKind.Explicit || argument.Parameter is not { } parameter) return false;
            if (HasParameterFilter(parameter)) return false;
            if (!IsResolveOf(argument.Value, parameter.Type, contextParameter, resolution)) return false;
        }

        return true;
    }

    private static bool HasSingleRegistrableConstructor(INamedTypeSymbol type)
    {
        int count = 0;
        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            if (constructor.DeclaredAccessibility == Accessibility.Public) count++;
        }

        return count == 1;
    }

    private static bool HasParameterFilter(IParameterSymbol parameter)
    {
        foreach (AttributeData attribute in parameter.GetAttributes())
        {
            for (INamedTypeSymbol? current = attribute.AttributeClass; current is not null; current = current.BaseType)
            {
                if (current is { Name: "ParameterFilterAttribute", ContainingNamespace: { Name: "AttributeFilters" } }) return true;
            }
        }

        return false;
    }

    private static bool IsResolveOf(IOperation value, ITypeSymbol parameterType, IParameterSymbol contextParameter, INamedTypeSymbol resolution)
    {
        if (value is not IInvocationOperation { TargetMethod: { Name: "Resolve", TypeArguments.Length: 1 } resolve } invocation) return false;
        if (!SymbolEqualityComparer.Default.Equals(resolve.ContainingType, resolution)) return false;
        if (!SymbolEqualityComparer.Default.Equals(resolve.TypeArguments[0], parameterType)) return false;
        if (invocation.Arguments.Length != 1) return false;

        IOperation receiver = invocation.Arguments[0].Value;

        while (receiver is IConversionOperation { IsImplicit: true } conversion)
            receiver = conversion.Operand;

        return receiver is IParameterReferenceOperation reference
            && SymbolEqualityComparer.Default.Equals(reference.Parameter, contextParameter);
    }
}
