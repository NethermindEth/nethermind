// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record PrecompileGasPricingAdapterShape(
    string StandardPolicyMethod,
    string KernelMethod,
    string FullFrameMethod,
    string InlineMethod,
    string ConcreteVirtualMachine,
    string MainnetDiRegistration,
    string MainnetDiRegistrationExtension,
    string MainnetDiRegistrationAssembly,
    IReadOnlyList<string> PricingSteps,
    bool BaseThenData,
    bool OnlyExecutionGasIsAssigned,
    bool FullFrameUsesLocalCopy,
    bool InlineUsesByRefChild,
    bool EthereumVirtualMachineUsesStandardGasPolicy,
    bool MainnetDiRegistersEthereumVirtualMachine,
    bool MainnetDiBindsProductionScopedExtension);

internal static class PrecompileGasPricingAdapterValidator
{
    private const string StandardPolicyMethodName = "TryConsumePrecompileGas";
    private const string KernelTypeName = "Nethermind.Evm.GasPolicy.PrecompileGasPricingKernel";
    private const string ResultTypeName = "Nethermind.Evm.GasPolicy.PrecompileGasPricingResult";
    private const string FullFrameMethodName = "RunPrecompile";
    private const string InlineMethodName = "TryInlineStaticPrecompileCall";
    private const string MainnetDiExtensionTypeName = "Nethermind.Core.ContainerBuilderExtensions";
    private const string MainnetDiExtensionMethodName = "AddScoped";
    private const string MainnetDiExtensionAssemblyName = "Nethermind.Core";

    private const string AdapterPrelude = """
        using System;
        using System.Runtime.CompilerServices;
        using Nethermind.Core.Specs;
        using Nethermind.Evm.Precompiles;

        namespace Nethermind.Core.Specs
        {
            public interface IReleaseSpec;
        }

        namespace Nethermind.Evm.Precompiles
        {
            public interface IPrecompile
            {
                ulong BaseGasCost(IReleaseSpec releaseSpec);
                ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);
            }
        }

        namespace Nethermind.Evm.GasPolicy
        {
            public struct EthereumGasPolicy
            {
                public ulong Value;
        """;

    private const string AdapterPostlude = """
            }
        }
        """;

    private const string MainnetDiAutofacPrelude = """
        namespace Autofac
        {
            public class ContainerBuilder
            {
            }

            public abstract class Module
            {
                protected virtual void Load(ContainerBuilder builder)
                {
                }
            }
        }
        """;

    private const string MainnetDiEvmPrelude = """
        namespace Nethermind.Evm
        {
            public interface IVirtualMachine
            {
            }

            public sealed class EthereumVirtualMachine : IVirtualMachine
            {
            }
        }
        """;

    public static PrecompileGasPricingAdapterShape Validate(
        string ethereumGasPolicySourcePath,
        string gasPolicySourcePath,
        string virtualMachineSourcePath,
        string inlineCallSourcePath,
        string mainnetDiSourcePath,
        string mainnetDiExtensionSourcePath,
        string kernelSourcePath)
    {
        CompilationUnitSyntax policyRoot = Parse(ethereumGasPolicySourcePath, "Precompile gas pricing policy adapter", rejectAllDirectives: true);
        CompilationUnitSyntax gasPolicyRoot = Parse(gasPolicySourcePath, "Precompile gas pricing policy contract", rejectAllDirectives: true);
        CompilationUnitSyntax virtualMachineRoot = Parse(virtualMachineSourcePath, "Precompile gas pricing full-frame caller", rejectAllDirectives: false);
        CompilationUnitSyntax inlineRoot = Parse(inlineCallSourcePath, "Precompile gas pricing inline caller", rejectAllDirectives: true);
        CompilationUnitSyntax mainnetDiRoot = Parse(mainnetDiSourcePath, "Precompile gas pricing mainnet DI registration", rejectAllDirectives: true);
        RejectVirtualMachineDirectiveVariants(virtualMachineRoot);
        MethodDeclarationSyntax policyMethod = ValidatePolicyShape(policyRoot, kernelSourcePath);
        ValidatePolicySemanticBinding(policyMethod, kernelSourcePath);
        ValidatePolicyContract(gasPolicyRoot);
        ValidateFullFrameCallSite(virtualMachineRoot);
        ValidateInlineCallSite(inlineRoot);
        ValidateMainnetDiRegistration(mainnetDiRoot, mainnetDiExtensionSourcePath);

        return new PrecompileGasPricingAdapterShape(
            StandardPolicyMethod: "EthereumGasPolicy.TryConsumePrecompileGas",
            KernelMethod: "PrecompileGasPricingKernel.TryConsume",
            FullFrameMethod: "VirtualMachine.RunPrecompile",
            InlineMethod: "EvmInstructions.TryInlineStaticPrecompileCall",
            ConcreteVirtualMachine: "EthereumVirtualMachine : VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>",
            MainnetDiRegistration: ".AddScoped<IVirtualMachine, EthereumVirtualMachine>()",
            MainnetDiRegistrationExtension:
                "Nethermind.Core.ContainerBuilderExtensions.AddScoped<Nethermind.Evm.IVirtualMachine, Nethermind.Evm.EthereumVirtualMachine>()",
            MainnetDiRegistrationAssembly: MainnetDiExtensionAssemblyName,
            PricingSteps:
            [
                "precompile base cost",
                "precompile data cost",
                "pure overflow and affordability decision",
                "execution gas assignment only",
            ],
            BaseThenData: true,
            OnlyExecutionGasIsAssigned: true,
            FullFrameUsesLocalCopy: true,
            InlineUsesByRefChild: true,
            EthereumVirtualMachineUsesStandardGasPolicy: true,
            MainnetDiRegistersEthereumVirtualMachine: true,
            MainnetDiBindsProductionScopedExtension: true);
    }

    private static CompilationUnitSyntax Parse(string sourcePath, string subject, bool rejectAllDirectives)
    {
        byte[] source = File.ReadAllBytes(sourcePath);
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp14)
            .WithDocumentationMode(DocumentationMode.Parse)
            .WithKind(SourceCodeKind.Regular);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            SourceText.From(source, source.Length, Encoding.UTF8, canBeEmbedded: true),
            parseOptions,
            sourcePath);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        Diagnostic[] errors = tree.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException(
                $"{subject} did not parse without diagnostics: " +
                string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        }

        if (rejectAllDirectives && root.DescendantTrivia(descendIntoTrivia: true).Any(static trivia => trivia.IsDirective))
        {
            throw new ExtractionException($"{subject} must not contain preprocessor directives.");
        }

        return root;
    }

    private static void RejectVirtualMachineDirectiveVariants(CompilationUnitSyntax root)
    {
        SyntaxTrivia[] trivia = root.DescendantTrivia(descendIntoTrivia: true).ToArray();
        if (trivia.Any(static item => item.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            throw new ExtractionException("Precompile gas pricing full-frame caller must not contain disabled source text.");
        }

        PragmaWarningDirectiveTriviaSyntax[] diagnosticsPragmas = trivia
            .Where(static item => item.IsDirective)
            .Select(static item => item.GetStructure())
            .OfType<PragmaWarningDirectiveTriviaSyntax>()
            .ToArray();
        int directiveCount = trivia.Count(static item => item.IsDirective);
        if (directiveCount != 2 || diagnosticsPragmas.Length != 2 ||
            !IsPinnedDiagnosticsPragma(diagnosticsPragmas[0], SyntaxKind.DisableKeyword) ||
            !IsPinnedDiagnosticsPragma(diagnosticsPragmas[1], SyntaxKind.RestoreKeyword))
        {
            throw new ExtractionException(
                "Precompile gas pricing full-frame caller must contain only the two pinned diagnostics pragmas and no conditional directives.");
        }
    }

    private static bool IsPinnedDiagnosticsPragma(
        PragmaWarningDirectiveTriviaSyntax directive,
        SyntaxKind action) =>
        directive.DisableOrRestoreKeyword.IsKind(action) &&
        directive.ErrorCodes.Count == 1 &&
        directive.ErrorCodes[0] is IdentifierNameSyntax { Identifier.ValueText: "IDE0063" };

    private static MethodDeclarationSyntax ValidatePolicyShape(CompilationUnitSyntax root, string kernelSourcePath)
    {
        StructDeclarationSyntax policy = RequireSingle(
            root.DescendantNodes().OfType<StructDeclarationSyntax>()
                .Where(static declaration => declaration.Identifier.ValueText == "EthereumGasPolicy"),
            "Precompile gas pricing policy adapter must declare EthereumGasPolicy exactly once.");
        if (ContainingNamespace(policy) != "Nethermind.Evm.GasPolicy" ||
            !policy.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            policy.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            policy.Ancestors().OfType<TypeDeclarationSyntax>().Any())
        {
            throw new ExtractionException("Precompile gas pricing policy adapter type changed its pinned shape.");
        }

        RejectShadowDeclarations(root, kernelSourcePath);
        MethodDeclarationSyntax method = FindUniqueMethod(policy, StandardPolicyMethodName, genericParameterCount: 0);
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            method.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            Canonical(method.ReturnType) != "bool" ||
            method.ParameterList.Parameters.Count != 4)
        {
            throw new ExtractionException("Precompile gas pricing policy method changed its pinned public static signature.");
        }

        ValidateParameter(method.ParameterList.Parameters[0], "gas", "EthereumGasPolicy", "ref");
        ValidateParameter(method.ParameterList.Parameters[1], "precompile", "IPrecompile");
        ValidateParameter(method.ParameterList.Parameters[2], "inputData", "ReadOnlyMemory<byte>");
        ValidateParameter(method.ParameterList.Parameters[3], "spec", "IReleaseSpec");
        RejectDirectivesWithin(method, "Precompile gas pricing policy adapter");
        ValidatePolicyBody(method);
        return method;
    }

    private static void RejectShadowDeclarations(CompilationUnitSyntax root, string kernelSourcePath)
    {
        string[] forbidden = ["PrecompileGasPricingKernel", "PrecompileGasPricingResult", "PrecompileGasPricingOutcome"];
        if (root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(declaration =>
                forbidden.Contains(declaration.Identifier.ValueText, StringComparer.Ordinal)))
        {
            throw new ExtractionException("Precompile gas pricing policy adapter must not shadow the extracted kernel types.");
        }

        if (!File.Exists(kernelSourcePath))
        {
            throw new ExtractionException("Precompile gas pricing extracted kernel source was not found.");
        }
    }

    private static void ValidatePolicyBody(MethodDeclarationSyntax method)
    {
        if (method.Body is not
            {
                Statements:
                [
                    LocalDeclarationStatementSyntax baseCost,
                    LocalDeclarationStatementSyntax dataCost,
                    LocalDeclarationStatementSyntax result,
                    ExpressionStatementSyntax assignment,
                    ReturnStatementSyntax { Expression: not null } returnStatement,
                ],
            })
        {
            throw new ExtractionException("Precompile gas pricing policy adapter must retain its five ordered pricing steps.");
        }

        ValidateLocal(baseCost, "ulong", "baseGasCost", "precompile.BaseGasCost(spec)");
        ValidateLocal(dataCost, "ulong", "dataGasCost", "precompile.DataGasCost(inputData,spec)");
        ValidateLocal(
            result,
            "PrecompileGasPricingResult",
            "result",
            "PrecompileGasPricingKernel.TryConsume(gas.Value,baseGasCost,dataGasCost)");
        if (Canonical(assignment.Expression) != "gas.Value=result.RemainingGas" ||
            Canonical(returnStatement.Expression) != "result.OutcomeisPrecompileGasPricingOutcome.Success")
        {
            throw new ExtractionException("Precompile gas pricing policy adapter must assign only the kernel remaining gas and return its success outcome.");
        }
    }

    private static void ValidatePolicySemanticBinding(MethodDeclarationSyntax sourceMethod, string kernelSourcePath)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp14)
            .WithDocumentationMode(DocumentationMode.Parse)
            .WithKind(SourceCodeKind.Regular);
        byte[] kernelSource = File.ReadAllBytes(kernelSourcePath);
        SyntaxTree kernelTree = CSharpSyntaxTree.ParseText(
            SourceText.From(kernelSource, kernelSource.Length, Encoding.UTF8, canBeEmbedded: true),
            parseOptions,
            kernelSourcePath);
        string adapterSource = AdapterPrelude + Environment.NewLine + sourceMethod.NormalizeWhitespace().ToFullString() +
            Environment.NewLine + AdapterPostlude;
        SyntaxTree adapterTree = CSharpSyntaxTree.ParseText(adapterSource, parseOptions, "<precompile-gas-pricing-adapter>");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Nethermind.Evm.Lean.PrecompileGasPricingAdapter",
            [kernelTree, adapterTree],
            StateGasChargeExtractor.GetPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                checkOverflow: false,
                allowUnsafe: false,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true));
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException(
                "Precompile gas pricing policy adapter did not bind without diagnostics: " +
                string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        }

        MethodDeclarationSyntax semanticMethod = RequireSingle(
            adapterTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(static method => method.Identifier.ValueText == StandardPolicyMethodName),
            "Precompile gas pricing semantic adapter must declare TryConsumePrecompileGas exactly once.");
        SemanticModel semanticModel = compilation.GetSemanticModel(adapterTree);
        InvocationExpressionSyntax[] invocations = semanticMethod.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        if (invocations.Length != 3)
        {
            throw new ExtractionException("Precompile gas pricing policy adapter must contain exactly base, data, and kernel invocations.");
        }

        RequireInvocationTarget(semanticModel, invocations[0], "Nethermind.Evm.Precompiles.IPrecompile", "BaseGasCost", expectedSourcePath: null);
        RequireInvocationTarget(semanticModel, invocations[1], "Nethermind.Evm.Precompiles.IPrecompile", "DataGasCost", expectedSourcePath: null);
        RequireInvocationTarget(semanticModel, invocations[2], KernelTypeName, "TryConsume", kernelSourcePath);

        AssignmentExpressionSyntax assignment = RequireSingle(
            semanticMethod.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            "Precompile gas pricing policy adapter must contain exactly one assignment.");
        ISimpleAssignmentOperation assignmentOperation = semanticModel.GetOperation(assignment) as ISimpleAssignmentOperation
            ?? throw new ExtractionException("Precompile gas pricing policy adapter assignment did not bind.");
        if (assignmentOperation.Target is not IFieldReferenceOperation { Field.Name: "Value", Field.ContainingType.Name: "EthereumGasPolicy" } ||
            assignmentOperation.Value is not IFieldReferenceOperation { Field.Name: "RemainingGas", Field.ContainingType: { } resultType } ||
            resultType.ToDisplayString() != ResultTypeName)
        {
            throw new ExtractionException("Precompile gas pricing policy adapter must assign only EthereumGasPolicy.Value from the pinned result.");
        }

    }

    private static void RequireInvocationTarget(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        string expectedContainingType,
        string expectedMethod,
        string? expectedSourcePath)
    {
        IMethodSymbol target = (semanticModel.GetOperation(invocation) as IInvocationOperation)?.TargetMethod
            ?? throw new ExtractionException("Precompile gas pricing adapter invocation did not bind.");
        if (target.ContainingType.ToDisplayString() != expectedContainingType || target.Name != expectedMethod)
        {
            throw new ExtractionException("Precompile gas pricing adapter invocation did not bind to the pinned target.");
        }

        if (expectedSourcePath is not null && !target.DeclaringSyntaxReferences.Any(reference =>
                string.Equals(
                    Path.GetFullPath(reference.SyntaxTree.FilePath),
                    Path.GetFullPath(expectedSourcePath),
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ExtractionException("Precompile gas pricing adapter kernel invocation is not declared by the extracted source.");
        }
    }

    private static void ValidatePolicyContract(CompilationUnitSyntax root)
    {
        InterfaceDeclarationSyntax contract = RequireSingle(
            root.DescendantNodes().OfType<InterfaceDeclarationSyntax>()
                .Where(static declaration => declaration.Identifier.ValueText == "IGasPolicy"),
            "Precompile gas pricing policy contract must declare IGasPolicy exactly once.");
        MethodDeclarationSyntax method = FindUniqueMethod(contract, StandardPolicyMethodName, genericParameterCount: 0);
        if (!method.Modifiers.Any(SyntaxKind.StaticKeyword) || !method.Modifiers.Any(SyntaxKind.VirtualKeyword) ||
            method.Modifiers.Any(SyntaxKind.AbstractKeyword) || Canonical(method.ReturnType) != "bool" ||
            method.ParameterList.Parameters.Count != 4 || method.Body is not { Statements.Count: 3 } body)
        {
            throw new ExtractionException("Precompile gas pricing policy contract changed its pinned static virtual shape.");
        }

        ValidateParameter(method.ParameterList.Parameters[0], "gas", "TSelf", "ref");
        ValidateParameter(method.ParameterList.Parameters[1], "precompile", "IPrecompile");
        ValidateParameter(method.ParameterList.Parameters[2], "inputData", "ReadOnlyMemory<byte>");
        ValidateParameter(method.ParameterList.Parameters[3], "spec", "IReleaseSpec");
        ValidateLocal((LocalDeclarationStatementSyntax)body.Statements[0], "ulong", "baseGasCost", "precompile.BaseGasCost(spec)");
        ValidateLocal((LocalDeclarationStatementSyntax)body.Statements[1], "ulong", "dataGasCost", "precompile.DataGasCost(inputData,spec)");
        if (body.Statements[2] is not ReturnStatementSyntax { Expression: not null } result ||
            Canonical(result.Expression) !=
            "baseGasCost<=ulong.MaxValue-dataGasCost&&TSelf.UpdateGas(refgas,baseGasCost+dataGasCost)")
        {
            throw new ExtractionException("Precompile gas pricing policy contract must retain base-before-data overflow guarding and delegated debit.");
        }
    }

    private static void ValidateFullFrameCallSite(CompilationUnitSyntax root)
    {
        ClassDeclarationSyntax genericMachine = RequireSingle(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static declaration => declaration.Identifier.ValueText == "VirtualMachine" &&
                    declaration.TypeParameterList?.Parameters.Count == 1),
            "Precompile gas pricing full-frame caller must declare generic VirtualMachine exactly once.");
        ValidateGasPolicyConstraint(genericMachine.ConstraintClauses, "Precompile gas pricing full-frame caller");
        MethodDeclarationSyntax method = FindUniqueMethod(genericMachine, FullFrameMethodName, genericParameterCount: 1);
        RejectDirectivesWithin(method, "Precompile gas pricing full-frame caller");
        LocalDeclarationStatementSyntax gasLocal = RequireSingle(
            method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                .Where(static statement => Canonical(statement) == "TGasPolicygas=state.Gas;"),
            "Precompile gas pricing full-frame caller must copy state gas to one local policy value.");
        InvocationExpressionSyntax pricing = FindUniqueInvocation(method, "TGasPolicy.TryConsumePrecompileGas");
        ValidateInvocationArguments(pricing, "refgas,precompile,callData,spec");
        if (method.Body is null)
        {
            throw new ExtractionException("Precompile gas pricing full-frame caller must retain a block body.");
        }

        IfStatementSyntax pricingFailure = RequireSingle(
            method.Body.Statements.OfType<IfStatementSyntax>()
                .Where(static statement => Canonical(statement.Condition) ==
                    "!TGasPolicy.TryConsumePrecompileGas(refgas,precompile,callData,spec)"),
            "Precompile gas pricing full-frame caller must retain one negated pricing failure branch.");
        if (pricingFailure.Else is not null ||
            pricingFailure.Statement is not BlockSyntax
            {
                Statements:
                [ReturnStatementSyntax { Expression: not null } failureReturn],
            } ||
            Canonical(failureReturn.Expression) !=
            "new(default,precompileSuccess:false,shouldRevert:true,EvmExceptionType.OutOfGas)" ||
            !pricingFailure.Condition.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.Span == pricing.Span))
        {
            throw new ExtractionException("Precompile gas pricing full-frame failure must return OutOfGas before state installation.");
        }

        ExpressionStatementSyntax install = RequireSingle(
            method.DescendantNodes().OfType<ExpressionStatementSyntax>()
                .Where(static statement => Canonical(statement) == "state.Gas=gas;"),
            "Precompile gas pricing full-frame caller must install one successful local gas value.");
        int pricingFailureIndex = method.Body.Statements.IndexOf(pricingFailure);
        if (gasLocal.SpanStart >= pricing.SpanStart || pricing.SpanStart >= install.SpanStart ||
            pricingFailureIndex < 0 || pricingFailureIndex + 1 >= method.Body.Statements.Count ||
            method.Body.Statements[pricingFailureIndex + 1] != install)
        {
            throw new ExtractionException("Precompile gas pricing full-frame caller must install gas immediately after its returning pricing failure branch.");
        }

        ValidateEthereumVirtualMachine(root);
    }

    private static void ValidateEthereumVirtualMachine(CompilationUnitSyntax root)
    {
        UsingDirectiveSyntax[] usingDirectives = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToArray();
        ClassDeclarationSyntax concreteMachine = RequireSingle(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static declaration => declaration.Identifier.ValueText == "EthereumVirtualMachine"),
            "Precompile gas pricing full-frame caller must declare EthereumVirtualMachine exactly once.");
        if (ContainingNamespace(concreteMachine) != "Nethermind.Evm" ||
            !concreteMachine.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !concreteMachine.Modifiers.Any(SyntaxKind.SealedKeyword) ||
            concreteMachine.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            concreteMachine.Ancestors().OfType<TypeDeclarationSyntax>().Any() ||
            concreteMachine.BaseList?.Types is not
            [BaseTypeSyntax gasPolicyBase, BaseTypeSyntax virtualMachineInterface] ||
            Canonical(gasPolicyBase.Type) != "VirtualMachine<EthereumGasPolicy>" ||
            Canonical(virtualMachineInterface.Type) != "IVirtualMachine")
        {
            throw new ExtractionException(
                "Precompile gas pricing full-frame caller must retain EthereumVirtualMachine : VirtualMachine<EthereumGasPolicy>, IVirtualMachine.");
        }

        if (usingDirectives.Any(static directive => directive.Alias is not null ||
                (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) && !IsPinnedVirtualMachineStaticUsing(directive))) ||
            usingDirectives.Count(IsPinnedVirtualMachineStaticUsing) != 1 ||
            root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(declaration =>
                declaration != concreteMachine && declaration.Identifier.ValueText == "EthereumGasPolicy") ||
            usingDirectives.Count(static directive => directive.Alias is null &&
                directive.Name is not null && Canonical(directive.Name) == "Nethermind.Evm.GasPolicy") != 1)
        {
            throw new ExtractionException("Precompile gas pricing full-frame caller must not contain aliases or shadow the standard EthereumGasPolicy import.");
        }

        ValidateConcreteVirtualMachineBinding(gasPolicyBase.Type, virtualMachineInterface.Type);
    }

    private static bool IsPinnedVirtualMachineStaticUsing(UsingDirectiveSyntax directive) =>
        directive.Alias is null &&
        directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) &&
        directive.Name is not null &&
        Canonical(directive.Name) == "Nethermind.Evm.VirtualMachineStatics";

    private static void ValidateConcreteVirtualMachineBinding(TypeSyntax gasPolicyBase, TypeSyntax virtualMachineInterface)
    {
        string source = $$"""
            using Nethermind.Evm.GasPolicy;

            namespace Nethermind.Evm.GasPolicy
            {
                public struct EthereumGasPolicy
                {
                }
            }

            namespace Nethermind.Evm
            {
                public interface IVirtualMachine
                {
                }

                public class VirtualMachine<TGasPolicy>
                {
                }

                public sealed class EthereumVirtualMachine : {{gasPolicyBase}}, {{virtualMachineInterface}}
                {
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14),
            "<precompile-gas-pricing-concrete-vm>");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Nethermind.Evm.Lean.PrecompileGasPricingConcreteVm",
            [tree],
            StateGasChargeExtractor.GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException("Precompile gas pricing EthereumVirtualMachine base did not bind without diagnostics.");
        }

        INamedTypeSymbol concrete = compilation.GetTypeByMetadataName("Nethermind.Evm.EthereumVirtualMachine")
            ?? throw new ExtractionException("Precompile gas pricing EthereumVirtualMachine semantic type was not found.");
        if (concrete.BaseType?.OriginalDefinition.ToDisplayString() != "Nethermind.Evm.VirtualMachine<TGasPolicy>" ||
            concrete.BaseType.TypeArguments is not [ITypeSymbol gasPolicy] ||
            gasPolicy.ToDisplayString() != "Nethermind.Evm.GasPolicy.EthereumGasPolicy" ||
            !concrete.Interfaces.Select(static @interface => @interface.ToDisplayString())
                .SequenceEqual(["Nethermind.Evm.IVirtualMachine"], StringComparer.Ordinal))
        {
            throw new ExtractionException(
                "Precompile gas pricing EthereumVirtualMachine must bind to VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>.");
        }
    }

    private static void ValidateMainnetDiRegistration(CompilationUnitSyntax root, string extensionSourcePath)
    {
        UsingDirectiveSyntax[] usingDirectives = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToArray();
        ClassDeclarationSyntax module = RequireSingle(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static declaration => declaration.Identifier.ValueText == "BlockProcessingModule"),
            "Precompile gas pricing mainnet DI registration must declare BlockProcessingModule exactly once.");
        if (ContainingNamespace(module) != "Nethermind.Init.Modules" ||
            !module.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            module.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            module.Ancestors().OfType<TypeDeclarationSyntax>().Any() ||
            module.BaseList?.Types is not [BaseTypeSyntax moduleBase] ||
            Canonical(moduleBase.Type) != "Module")
        {
            throw new ExtractionException("Precompile gas pricing mainnet DI module changed its pinned shape.");
        }

        if (usingDirectives.Any(static directive => directive.Alias is not null ||
                !directive.StaticKeyword.IsKind(SyntaxKind.None) || !directive.GlobalKeyword.IsKind(SyntaxKind.None)) ||
            usingDirectives.Count(static directive => directive.Alias is null &&
                directive.Name is not null && Canonical(directive.Name) == "Autofac") != 1 ||
            usingDirectives.Count(static directive => directive.Alias is null &&
                directive.Name is not null && Canonical(directive.Name) == "Nethermind.Core") != 1 ||
            usingDirectives.Count(static directive => directive.Alias is null &&
                directive.Name is not null && Canonical(directive.Name) == "Nethermind.Evm") != 1 ||
            root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(static declaration =>
                declaration.Identifier.ValueText is "ContainerBuilder" or "IVirtualMachine" or "EthereumVirtualMachine"))
        {
            throw new ExtractionException("Precompile gas pricing mainnet DI registration must not contain aliases, static imports, or shadow its ContainerBuilder and virtual-machine types.");
        }

        MethodDeclarationSyntax load = RequireSingle(
            module.Members.OfType<MethodDeclarationSyntax>()
                .Where(static method => method.Identifier.ValueText == "Load" &&
                    (method.TypeParameterList?.Parameters.Count ?? 0) == 0),
            "Precompile gas pricing mainnet DI module must declare its Load method exactly once.");
        if (!load.Modifiers.Any(SyntaxKind.ProtectedKeyword) ||
            !load.Modifiers.Any(SyntaxKind.OverrideKeyword) ||
            load.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            Canonical(load.ReturnType) != "void" ||
            load.ParameterList.Parameters is not [ParameterSyntax builder] ||
            builder.Identifier.ValueText != "builder" ||
            builder.Type is null ||
            Canonical(builder.Type) != "ContainerBuilder" ||
            load.Body is null)
        {
            throw new ExtractionException("Precompile gas pricing mainnet DI Load method changed its pinned shape.");
        }

        RejectDirectivesWithin(load, "Precompile gas pricing mainnet DI registration");
        InvocationExpressionSyntax registration = RequireSingle(
            load.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(IsVirtualMachineScopedRegistration),
            "Precompile gas pricing mainnet DI registration must retain exactly one IVirtualMachine AddScoped call.");
        if (registration.Expression is not MemberAccessExpressionSyntax
            {
                Expression: ExpressionSyntax receiver,
                Name: GenericNameSyntax
                {
                    Identifier.ValueText: "AddScoped",
                    TypeArgumentList.Arguments: [TypeSyntax serviceType, TypeSyntax implementationType],
                },
            } ||
            Canonical(serviceType) != "IVirtualMachine" ||
            Canonical(implementationType) != "EthereumVirtualMachine" ||
            registration.ArgumentList.Arguments.Count != 0 ||
            !IsFluentBuilderChain(receiver, "builder"))
        {
            throw new ExtractionException(
                "Precompile gas pricing mainnet DI registration must retain builder.AddScoped<IVirtualMachine, EthereumVirtualMachine>().");
        }

        ValidateMainnetDiSemanticBinding(root, registration, extensionSourcePath);
    }

    private static void ValidateMainnetDiSemanticBinding(
        CompilationUnitSyntax root,
        InvocationExpressionSyntax registration,
        string extensionSourcePath)
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp14)
            .WithDocumentationMode(DocumentationMode.Parse)
            .WithKind(SourceCodeKind.Regular);
        MethodDeclarationSyntax extensionMethod = ValidateMainnetDiExtensionSource(extensionSourcePath);
        MetadataReference autofacReference = CompileMetadataReference(
            "Autofac",
            [CSharpSyntaxTree.ParseText(MainnetDiAutofacPrelude, parseOptions, "<precompile-gas-pricing-autofac>")],
            StateGasChargeExtractor.GetPlatformReferences(),
            "Precompile gas pricing Autofac binding");
        MetadataReference evmReference = CompileMetadataReference(
            "Nethermind.Evm",
            [CSharpSyntaxTree.ParseText(MainnetDiEvmPrelude, parseOptions, "<precompile-gas-pricing-evm>")],
            StateGasChargeExtractor.GetPlatformReferences(),
            "Precompile gas pricing EVM binding");
        MetadataReference extensionReference = CompileMainnetDiExtensionReference(
            extensionMethod,
            extensionSourcePath,
            parseOptions,
            autofacReference);
        SyntaxTree diTree = CSharpSyntaxTree.ParseText(
            CreateMainnetDiSemanticSource(root, registration),
            parseOptions,
            root.SyntaxTree.FilePath);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Nethermind.Init",
            [diTree],
            StateGasChargeExtractor.GetPlatformReferences()
                .Append(autofacReference)
                .Append(evmReference)
                .Append(extensionReference),
            CreateSemanticCompilationOptions());
        RejectBindingDiagnostics(compilation, "Precompile gas pricing mainnet DI registration");

        MethodDeclarationSyntax load = RequireSingle(
            diTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(static method => method.Identifier.ValueText == "Load"),
            "Precompile gas pricing mainnet DI semantic binding must declare Load exactly once.");
        ParameterSyntax builder = load.ParameterList.Parameters.Single();
        SemanticModel semanticModel = compilation.GetSemanticModel(diTree);
        IParameterSymbol builderSymbol = semanticModel.GetDeclaredSymbol(builder)
            ?? throw new ExtractionException("Precompile gas pricing mainnet DI builder did not bind.");
        if (builderSymbol.Type.ToDisplayString() != "Autofac.ContainerBuilder")
        {
            throw new ExtractionException("Precompile gas pricing mainnet DI registration must bind its builder to Autofac.ContainerBuilder.");
        }

        InvocationExpressionSyntax boundRegistration = RequireSingle(
            load.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(IsVirtualMachineScopedRegistration),
            "Precompile gas pricing mainnet DI semantic binding must retain exactly one IVirtualMachine registration.");
        IInvocationOperation binding = semanticModel.GetOperation(boundRegistration) as IInvocationOperation
            ?? throw new ExtractionException("Precompile gas pricing mainnet DI registration did not bind.");
        IMethodSymbol target = binding.TargetMethod;
        IMethodSymbol declaration = target.ReducedFrom ?? target;
        if (!target.IsExtensionMethod ||
            declaration.ContainingType.ToDisplayString() != MainnetDiExtensionTypeName ||
            declaration.ContainingAssembly.Name != MainnetDiExtensionAssemblyName ||
            declaration.Name != MainnetDiExtensionMethodName ||
            target.TypeArguments is not [ITypeSymbol service, ITypeSymbol implementation] ||
            service.ToDisplayString() != "Nethermind.Evm.IVirtualMachine" ||
            implementation.ToDisplayString() != "Nethermind.Evm.EthereumVirtualMachine")
        {
            throw new ExtractionException(
                "Precompile gas pricing mainnet DI registration must bind to the pinned Nethermind.Core ContainerBuilderExtensions.AddScoped target.");
        }
    }

    private static MethodDeclarationSyntax ValidateMainnetDiExtensionSource(string extensionSourcePath)
    {
        CompilationUnitSyntax root = Parse(
            extensionSourcePath,
            "Precompile gas pricing mainnet DI registration extension",
            rejectAllDirectives: true);
        if (root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(static directive =>
                directive.Alias is not null || !directive.StaticKeyword.IsKind(SyntaxKind.None) ||
                !directive.GlobalKeyword.IsKind(SyntaxKind.None)) ||
            root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(static declaration =>
                declaration.Identifier.ValueText == "ContainerBuilder"))
        {
            throw new ExtractionException("Precompile gas pricing mainnet DI registration extension must not contain aliases, static imports, or a ContainerBuilder shadow.");
        }

        ClassDeclarationSyntax extensions = RequireSingle(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static declaration => declaration.Identifier.ValueText == "ContainerBuilderExtensions"),
            "Precompile gas pricing mainnet DI registration extension must declare ContainerBuilderExtensions exactly once.");
        if (ContainingNamespace(extensions) != "Nethermind.Core" ||
            !extensions.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !extensions.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            extensions.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            extensions.Ancestors().OfType<TypeDeclarationSyntax>().Any())
        {
            throw new ExtractionException("Precompile gas pricing mainnet DI registration extension changed its pinned type shape.");
        }

        MethodDeclarationSyntax method = RequireSingle(
            extensions.Members.OfType<MethodDeclarationSyntax>().Where(IsPinnedScopedRegistrationExtension),
            "Precompile gas pricing mainnet DI registration extension must declare its pinned AddScoped<T, TImpl> method exactly once.");
        RejectDirectivesWithin(method, "Precompile gas pricing mainnet DI registration extension");
        return method;
    }

    private static bool IsPinnedScopedRegistrationExtension(MethodDeclarationSyntax method)
    {
        if (method.Identifier.ValueText != MainnetDiExtensionMethodName ||
            !method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            method.Modifiers.Any(SyntaxKind.PartialKeyword) ||
            Canonical(method.ReturnType) != "ContainerBuilder" ||
            method.TypeParameterList?.Parameters is not
            [
            { Identifier.ValueText: "T" },
            { Identifier.ValueText: "TImpl" },
            ] ||
            method.ParameterList.Parameters is not [ParameterSyntax builder] ||
            builder.Identifier.ValueText != "builder" || builder.Type is null ||
            Canonical(builder.Type) != "ContainerBuilder" ||
            !builder.Modifiers.Any(SyntaxKind.ThisKeyword) ||
            !method.ConstraintClauses.Select(Canonical).SequenceEqual(
                ["whereTImpl:T", "whereT:notnull"],
                StringComparer.Ordinal) ||
            method.Body is null || method.ExpressionBody is not null)
        {
            return false;
        }

        return true;
    }

    private static MetadataReference CompileMainnetDiExtensionReference(
        MethodDeclarationSyntax extensionMethod,
        string extensionSourcePath,
        CSharpParseOptions parseOptions,
        MetadataReference autofacReference)
    {
        SyntaxTree extensionTree = CSharpSyntaxTree.ParseText(
            CreateMainnetDiExtensionSource(extensionMethod),
            parseOptions,
            extensionSourcePath);
        CSharpCompilation compilation = CSharpCompilation.Create(
            MainnetDiExtensionAssemblyName,
            [extensionTree],
            StateGasChargeExtractor.GetPlatformReferences().Append(autofacReference),
            CreateSemanticCompilationOptions());
        RejectBindingDiagnostics(compilation, "Precompile gas pricing mainnet DI registration extension");
        INamedTypeSymbol extensions = compilation.GetTypeByMetadataName(MainnetDiExtensionTypeName)
            ?? throw new ExtractionException("Precompile gas pricing mainnet DI registration extension semantic type was not found.");
        IMethodSymbol method = RequireSingle(
            extensions.GetMembers().OfType<IMethodSymbol>().Where(static candidate =>
                candidate.Name == MainnetDiExtensionMethodName && candidate.TypeParameters.Length == 2 &&
                candidate.Parameters.Length == 1),
            "Precompile gas pricing mainnet DI registration extension semantic target was not found.");
        if (method.ContainingAssembly.Name != MainnetDiExtensionAssemblyName ||
            !method.DeclaringSyntaxReferences.Any(reference => string.Equals(
                Path.GetFullPath(reference.SyntaxTree.FilePath),
                Path.GetFullPath(extensionSourcePath),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ExtractionException("Precompile gas pricing mainnet DI registration extension did not retain its pinned source and assembly identity.");
        }

        return CompileMetadataReference(
            MainnetDiExtensionAssemblyName,
            [extensionTree],
            StateGasChargeExtractor.GetPlatformReferences().Append(autofacReference),
            "Precompile gas pricing mainnet DI registration extension");
    }

    private static string CreateMainnetDiExtensionSource(MethodDeclarationSyntax method)
    {
        MethodDeclarationSyntax semanticMethod = method
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(
                SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("builder"))));
        return $$"""
            using Autofac;

            namespace Nethermind.Core
            {
                public static class ContainerBuilderExtensions
                {
            {{semanticMethod.NormalizeWhitespace().ToFullString()}}
                }
            }
            """;
    }

    private static string CreateMainnetDiSemanticSource(
        CompilationUnitSyntax root,
        InvocationExpressionSyntax registration)
    {
        string imports = string.Join(
            Environment.NewLine,
            root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(IsMainnetDiBindingUsing)
                .Select(static directive => directive.NormalizeWhitespace().ToFullString()));
        string localExtensions = string.Join(
            Environment.NewLine,
            root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static declaration => !declaration.Ancestors().OfType<TypeDeclarationSyntax>().Any())
                .Where(static declaration => declaration.Modifiers.Any(SyntaxKind.StaticKeyword))
                .Where(static declaration => declaration.Members.OfType<MethodDeclarationSyntax>().Any(IsContainerBuilderExtension))
                .Select(static declaration => declaration.NormalizeWhitespace().ToFullString()));
        InvocationExpressionSyntax semanticRegistration = CreateMainnetDiSemanticRegistration(registration);
        return $$"""
            {{imports}}

            namespace Nethermind.Init.Modules
            {
                public sealed class BlockProcessingModule : Module
                {
                    protected override void Load(ContainerBuilder builder)
                    {
                        {{semanticRegistration.NormalizeWhitespace().ToFullString()}};
                    }
                }

            {{localExtensions}}
            }
            """;
    }

    private static InvocationExpressionSyntax CreateMainnetDiSemanticRegistration(
        InvocationExpressionSyntax registration)
    {
        if (registration.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            throw new ExtractionException("Precompile gas pricing mainnet DI registration semantic target changed shape.");
        }

        return registration.WithExpression(memberAccess.WithExpression(SyntaxFactory.IdentifierName("builder")));
    }

    private static bool IsMainnetDiBindingUsing(UsingDirectiveSyntax directive)
    {
        if (directive.Alias is not null || !directive.StaticKeyword.IsKind(SyntaxKind.None) ||
            !directive.GlobalKeyword.IsKind(SyntaxKind.None) || directive.Name is null)
        {
            return false;
        }

        string name = Canonical(directive.Name);
        return name is "Autofac" or "Nethermind.Core" or "Nethermind.Evm";
    }

    private static bool IsContainerBuilderExtension(MethodDeclarationSyntax method)
    {
        if (!method.Modifiers.Any(SyntaxKind.StaticKeyword) || method.ParameterList.Parameters.Count == 0)
        {
            return false;
        }

        ParameterSyntax receiver = method.ParameterList.Parameters[0];
        return receiver.Type is not null && receiver.Modifiers.Any(SyntaxKind.ThisKeyword) &&
            Canonical(receiver.Type) == "ContainerBuilder";
    }

    private static MetadataReference CompileMetadataReference(
        string assemblyName,
        IReadOnlyList<SyntaxTree> syntaxTrees,
        IEnumerable<MetadataReference> references,
        string subject)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            CreateSemanticCompilationOptions());
        RejectBindingDiagnostics(compilation, subject);
        using MemoryStream image = new();
        EmitResult result = compilation.Emit(image);
        if (!result.Success)
        {
            throw new ExtractionException(
                $"{subject} did not emit without diagnostics: " +
                string.Join(Environment.NewLine, result.Diagnostics
                    .Where(static diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(static diagnostic => diagnostic.ToString())));
        }

        return MetadataReference.CreateFromImage(ImmutableArray.Create(image.ToArray()));
    }

    private static CSharpCompilationOptions CreateSemanticCompilationOptions() =>
        new(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            checkOverflow: false,
            allowUnsafe: false,
            nullableContextOptions: NullableContextOptions.Enable,
            deterministic: true);

    private static void RejectBindingDiagnostics(Compilation compilation, string subject)
    {
        Diagnostic[] errors = compilation.GetDiagnostics()
            .Where(static diagnostic => !diagnostic.IsSuppressed && diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new ExtractionException(
                $"{subject} did not bind without diagnostics: " +
                string.Join(Environment.NewLine, errors.Select(static diagnostic => diagnostic.ToString())));
        }
    }

    private static bool IsVirtualMachineScopedRegistration(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax generic,
            } ||
            generic.Identifier.ValueText != "AddScoped" ||
            generic.TypeArgumentList.Arguments.Count < 1)
        {
            return false;
        }

        return Canonical(generic.TypeArgumentList.Arguments[0]) == "IVirtualMachine";
    }

    private static bool IsFluentBuilderChain(ExpressionSyntax expression, string parameterName) =>
        expression switch
        {
            IdentifierNameSyntax { Identifier.ValueText: var name } => name == parameterName,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: ExpressionSyntax receiver } } =>
                IsFluentBuilderChain(receiver, parameterName),
            _ => false,
        };

    private static void ValidateInlineCallSite(CompilationUnitSyntax root)
    {
        ClassDeclarationSyntax instructions = RequireSingle(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(static declaration => declaration.Identifier.ValueText == "EvmInstructions"),
            "Precompile gas pricing inline caller must declare EvmInstructions exactly once.");
        MethodDeclarationSyntax method = FindUniqueMethod(instructions, InlineMethodName, genericParameterCount: 2);
        ValidateGasPolicyConstraint(method.ConstraintClauses, "Precompile gas pricing inline caller");
        RejectDirectivesWithin(method, "Precompile gas pricing inline caller");
        LocalDeclarationStatementSyntax child = RequireSingle(
            method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                .Where(static statement => Canonical(statement) ==
                    "TGasPolicychildGas=TGasPolicy.CreateChildFrameGas(refgas,gasLimitUl);"),
            "Precompile gas pricing inline caller must create one child policy value before pricing.");
        InvocationExpressionSyntax pricing = FindUniqueInvocation(method, "TGasPolicy.TryConsumePrecompileGas");
        ValidateInvocationArguments(pricing, "refchildGas,precompile,callData,spec");
        IfStatementSyntax pricingFailure = RequireSingle(
            method.DescendantNodes().OfType<IfStatementSyntax>()
                .Where(static statement => Canonical(statement.Condition) ==
                    "!TGasPolicy.TryConsumePrecompileGas(refchildGas,precompile,callData,spec)"),
            "Precompile gas pricing inline caller must retain one precompile-pricing failure branch.");
        if (pricingFailure.Else is not null ||
            pricingFailure.Statement is not BlockSyntax
            {
                Statements:
                [
                    ExpressionStatementSyntax restoreStatement,
                    ExpressionStatementSyntax returnDataClear,
                    ExpressionStatementSyntax stackResult,
                    ReturnStatementSyntax { Expression: not null } returnStatement,
                ],
            } ||
            Canonical(restoreStatement.Expression) != "TGasPolicy.RestoreChildStateGasOnHalt(refgas,inchildGas)" ||
            Canonical(returnDataClear.Expression) != "vm.ReturnDataBuffer=default" ||
            Canonical(stackResult.Expression) != "result=stack.PushZero<TTracingInst,OnFlag>()" ||
            Canonical(returnStatement.Expression) != "true" ||
            !pricingFailure.Condition.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.Span == pricing.Span))
        {
            throw new ExtractionException("Precompile gas pricing inline failure must restore the priced child and return before any success path.");
        }

        InvocationExpressionSyntax restore = RequireSingle(
            pricingFailure.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(static invocation => Canonical(invocation.Expression) == "TGasPolicy.RestoreChildStateGasOnHalt"),
            "Precompile gas pricing inline caller must restore the priced child state in its pricing failure branch.");
        ValidateInvocationArguments(restore, "refgas,inchildGas");
        if (child.SpanStart >= pricing.SpanStart || pricing.SpanStart >= restore.SpanStart)
        {
            throw new ExtractionException("Precompile gas pricing inline caller must price its by-ref child before the OOG state restoration.");
        }
    }

    private static MethodDeclarationSyntax FindUniqueMethod(SyntaxNode root, string name, int genericParameterCount) =>
        RequireSingle(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == name &&
                    (method.TypeParameterList?.Parameters.Count ?? 0) == genericParameterCount),
            $"Precompile gas pricing source must contain exactly one '{name}' method with the requested generic shape.");

    private static InvocationExpressionSyntax FindUniqueInvocation(MethodDeclarationSyntax method, string expectedExpression) =>
        RequireSingle(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => Canonical(invocation.Expression) == expectedExpression),
            $"Precompile gas pricing source must retain exactly one '{expectedExpression}' invocation.");

    private static T RequireSingle<T>(IEnumerable<T> candidates, string requirement)
    {
        T[] matches = candidates.ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new ExtractionException($"{requirement} Found {matches.Length}.");
    }

    private static void ValidateInvocationArguments(InvocationExpressionSyntax invocation, string expected)
    {
        string actual = string.Join(",", invocation.ArgumentList.Arguments.Select(Canonical));
        if (actual != expected)
        {
            throw new ExtractionException("Precompile gas pricing call-site invocation arguments changed shape.");
        }
    }

    private static void ValidateGasPolicyConstraint(
        SyntaxList<TypeParameterConstraintClauseSyntax> clauses,
        string subject)
    {
        if (!clauses.Select(Canonical).Contains("whereTGasPolicy:struct,IGasPolicy<TGasPolicy>", StringComparer.Ordinal))
        {
            throw new ExtractionException($"{subject} must retain the static IGasPolicy constraint.");
        }
    }

    private static void ValidateParameter(ParameterSyntax parameter, string name, string type, string modifiers = "")
    {
        string actualModifiers = string.Concat(parameter.Modifiers.Select(static modifier => modifier.Kind() switch
        {
            SyntaxKind.RefKeyword => "ref",
            SyntaxKind.ReadOnlyKeyword => "readonly",
            SyntaxKind.OutKeyword => "out",
            SyntaxKind.InKeyword => "in",
            _ => modifier.Text,
        }));
        if (parameter.Identifier.ValueText != name || parameter.Type is null ||
            Canonical(parameter.Type) != type || actualModifiers != modifiers)
        {
            throw new ExtractionException($"Precompile gas pricing parameter '{name}' changed its pinned shape.");
        }
    }

    private static void ValidateLocal(StatementSyntax statement, string type, string name, string initializer)
    {
        if (statement is not LocalDeclarationStatementSyntax local ||
            Canonical(local.Declaration.Type) != type ||
            local.Declaration.Variables is not
            [{ Identifier.ValueText: var actualName, Initializer.Value: ExpressionSyntax actualInitializer }] ||
            actualName != name || Canonical(actualInitializer) != initializer)
        {
            throw new ExtractionException($"Precompile gas pricing local '{name}' changed its pinned shape.");
        }
    }

    private static void RejectDirectivesWithin(SyntaxNode node, string subject)
    {
        if (node.DescendantTrivia(descendIntoTrivia: true).Any(static trivia => trivia.IsDirective))
        {
            throw new ExtractionException($"{subject} must not contain preprocessor directives in the selected path.");
        }
    }

    private static string ContainingNamespace(SyntaxNode declaration) =>
        string.Join(
            ".",
            declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse()
                .Select(static syntax => syntax.Name.ToString()));

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));
}
