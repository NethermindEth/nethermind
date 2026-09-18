// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record AccountAccessPricingAdapterShape(
    string DelegationMethod,
    string CoreMethod,
    string CallSiteMethod,
    string CallSpecMethod,
    IReadOnlyList<string> CoreSteps,
    bool TracingWarmUpPrecedesFacts,
    bool PrecompileCheckOnlyAfterColdFact,
    bool GasUpdateFollowsPricing,
    bool DelegatedAccessIsSecondAndConditional,
    bool DelegatedAccessPathIsReachable);

internal static class AccountAccessPricingAdapterValidator
{
    private const string DelegationMethodName = "TryConsumeDelegatedAccountAccessGas";
    private const string CoreMethodName = "TryConsumeAccountAccessGasCore";
    private const string AccessMethod = "TryConsumeAccountAccessGas";
    private const string KernelDelegation =
        "AccountAccessPricingKernel.Price(hotAndColdEnabled,eip8038Enabled,isCold,isPrecompile,kind,isCold&&!isPrecompile?TColdCost.GasCost(spec):0,TMode.WarmAccountAccessGas(spec))";
    private const string GenericDelegationTarget =
        "TryConsumeAccountAccessGas<Eip2929,Eip8038>(refgas,spec,inaccessTracker,isTracingAccess,delegated)";

    public static AccountAccessPricingAdapterShape Validate(
        string ethereumGasPolicySourcePath,
        string callSourcePath,
        string specSourcePath)
    {
        CompilationUnitSyntax root = Parse(ethereumGasPolicySourcePath);
        CompilationUnitSyntax callRoot = Parse(callSourcePath);
        CompilationUnitSyntax specRoot = Parse(specSourcePath);
        MethodDeclarationSyntax delegation = FindUniqueMethod(root, DelegationMethodName, genericParameterCount: 2);
        MethodDeclarationSyntax core = FindUniqueMethod(root, CoreMethodName, genericParameterCount: 2);
        ValidateDelegationSignature(delegation);
        ValidateDelegationBody(delegation);
        ValidateCoreSignature(core);
        ValidateCoreBody(core);
        ValidateWarmSchedule(root);

        StructDeclarationSyntax callSpec = FindUniqueType(specRoot, "CallSpec", genericParameterCount: 5);
        MethodDeclarationSyntax callSpecDelegation = FindUniqueMethod(callSpec, DelegationMethodName, genericParameterCount: 1);
        ValidateCallSpecDelegation(callSpecDelegation);
        MethodDeclarationSyntax call = FindUniqueMethod(callRoot, "InstructionCall", genericParameterCount: 6);
        ValidateCallSite(call);

        return new AccountAccessPricingAdapterShape(
            DelegationMethodName,
            CoreMethodName,
            "InstructionCall",
            "CallSpec.TryConsumeDelegatedAccountAccessGas",
            [
                "hot/cold gate",
                "optional tracing WarmUp",
                "access WarmUp",
                "cold-gate precompile fact",
                "conditional EIP-8038 fact",
                "fork-aware warm account schedule",
                "pure account-access pricing",
                "gas update or no-charge decision",
            ],
            TracingWarmUpPrecedesFacts: true,
            PrecompileCheckOnlyAfterColdFact: true,
            GasUpdateFollowsPricing: true,
            DelegatedAccessIsSecondAndConditional: true,
            DelegatedAccessPathIsReachable: true);
    }

    private static CompilationUnitSyntax Parse(string sourcePath) =>
        CSharpSyntaxTree.ParseText(
            File.ReadAllText(sourcePath),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14),
            sourcePath).GetCompilationUnitRoot();

    private static MethodDeclarationSyntax FindUniqueMethod(
        SyntaxNode root,
        string methodName,
        int? genericParameterCount = null)
    {
        MethodDeclarationSyntax[] methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName &&
                (genericParameterCount is null || method.TypeParameterList?.Parameters.Count == genericParameterCount))
            .ToArray();
        return methods.Length == 1
            ? methods[0]
            : throw new ExtractionException($"Account-access adapter must contain exactly one '{methodName}' method with the requested generic shape.");
    }

    private static StructDeclarationSyntax FindUniqueType(SyntaxNode root, string typeName, int genericParameterCount)
    {
        StructDeclarationSyntax[] types = root.DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == typeName &&
                (type.TypeParameterList?.Parameters.Count ?? 0) == genericParameterCount)
            .ToArray();
        return types.Length == 1
            ? types[0]
            : throw new ExtractionException($"Account-access adapter must contain exactly one '{typeName}' type with the requested generic shape.");
    }

    private static void ValidateDelegationSignature(MethodDeclarationSyntax method)
    {
        if (!method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            Canonical(method.ReturnType) != "bool" ||
            method.TypeParameterList?.Parameters.Select(static parameter => parameter.Identifier.ValueText)
                .SequenceEqual(["Eip2929", "Eip8038"], StringComparer.Ordinal) != true ||
            method.ParameterList.Parameters.Count != 5)
        {
            throw new ExtractionException("Account-access generic delegation helper does not have the pinned public signature.");
        }

        ValidateParameter(method.ParameterList.Parameters[0], "gas", "EthereumGasPolicy", "ref");
        ValidateParameter(method.ParameterList.Parameters[1], "spec", "IReleaseSpec");
        ValidateParameter(method.ParameterList.Parameters[2], "accessTracker", "StackAccessTracker", "refreadonly");
        ValidateParameter(method.ParameterList.Parameters[3], "isTracingAccess", "bool");
        ValidateParameter(method.ParameterList.Parameters[4], "delegated", "Address?");
    }

    private static void ValidateDelegationBody(MethodDeclarationSyntax method)
    {
        if (method.Body is not null || method.ExpressionBody is null ||
            WithoutTrivia(method.ExpressionBody.Expression) !=
            "!Eip2929.IsActive||delegatedisnull||" + GenericDelegationTarget)
        {
            throw new ExtractionException("Account-access delegated-target helper must retain its conditional specialized access step.");
        }

        InvocationExpressionSyntax[] accessCalls = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression).Contains(AccessMethod, StringComparison.Ordinal))
            .ToArray();
        if (accessCalls.Length != 1 || Canonical(accessCalls[0]) != GenericDelegationTarget)
        {
            throw new ExtractionException("Account-access delegated-target helper must retain its specialized access step.");
        }
    }

    private static void ValidateCoreSignature(MethodDeclarationSyntax method)
    {
        if (!method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PrivateKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            Canonical(method.ReturnType) != "bool" ||
            method.TypeParameterList?.Parameters.Select(static parameter => parameter.Identifier.ValueText)
                .SequenceEqual(["TColdCost", "TMode"], StringComparer.Ordinal) != true ||
            method.ParameterList.Parameters.Count != 6)
        {
            throw new ExtractionException("Account-access core adapter does not have the pinned private generic signature.");
        }

        ValidateParameter(method.ParameterList.Parameters[0], "gas", "EthereumGasPolicy", "ref");
        ValidateParameter(method.ParameterList.Parameters[1], "spec", "IReleaseSpec");
        ValidateParameter(method.ParameterList.Parameters[2], "accessTracker", "StackAccessTracker", "refreadonly");
        ValidateParameter(method.ParameterList.Parameters[3], "isTracingAccess", "bool");
        ValidateParameter(method.ParameterList.Parameters[4], "address", "Address");
        ValidateParameter(method.ParameterList.Parameters[5], "kind", "AccountAccessKind");
    }

    private static void ValidateCoreBody(MethodDeclarationSyntax method)
    {
        if (method.Body is not
            {
                Statements:
                [
                    LocalDeclarationStatementSyntax hotAndCold,
                    IfStatementSyntax disabled,
                    IfStatementSyntax tracing,
                    LocalDeclarationStatementSyntax cold,
                    LocalDeclarationStatementSyntax precompile,
                    LocalDeclarationStatementSyntax eip8038,
                    LocalDeclarationStatementSyntax pricing,
                    ReturnStatementSyntax { Expression: not null } result,
                ],
            })
        {
            throw new ExtractionException("Account-access core adapter must retain its eight ordered steps.");
        }

        ValidateLocal(hotAndCold, "bool", "hotAndColdEnabled", "TMode.UseHotAndColdStorage(spec)");
        ValidateReturnGate(disabled, "!hotAndColdEnabled");
        if (WithoutTrivia(tracing.Condition) != "isTracingAccess" || tracing.Else is not null ||
            tracing.Statement is not BlockSyntax { Statements: [ExpressionStatementSyntax warmUp] } ||
            WithoutTrivia(warmUp.Expression) != "accessTracker.WarmUp(address)")
        {
            throw new ExtractionException("Account-access core adapter must perform tracing WarmUp before access facts.");
        }

        ValidateLocal(cold, "bool", "isCold", "accessTracker.WarmUp(address)");
        ValidateLocal(precompile, "bool", "isPrecompile", "isCold&&spec.IsPrecompile(address)");
        ValidateLocal(
            eip8038,
            "bool",
            "eip8038Enabled",
            "kind==AccountAccessKind.SelfDestructBeneficiary&&(!isCold||isPrecompile)&&TMode.IsEip8038Enabled(spec)");
        ValidateLocal(pricing, "AccountAccessPricingResult", "pricing", KernelDelegation);
        if (WithoutTrivia(result.Expression) !=
            "pricing.Decision==AccountAccessPricingDecision.NoCharge||UpdateGas(refgas,pricing.Amount)")
        {
            throw new ExtractionException("Account-access core adapter must update gas only for the pure charge decision.");
        }

        InvocationExpressionSyntax[] warmUps = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) == "accessTracker.WarmUp")
            .ToArray();
        InvocationExpressionSyntax[] precompileChecks = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) == "spec.IsPrecompile")
            .ToArray();
        InvocationExpressionSyntax[] pricingCalls = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) == "AccountAccessPricingKernel.Price")
            .ToArray();
        if (warmUps.Length != 2 || precompileChecks.Length != 1 || pricingCalls.Length != 1 ||
            warmUps.Any(call => call.SpanStart >= pricingCalls[0].SpanStart) ||
            precompileChecks[0].SpanStart >= pricingCalls[0].SpanStart ||
            pricingCalls[0].SpanStart >= result.SpanStart)
        {
            throw new ExtractionException(
                "Account-access core adapter must retain exactly the tracing/access WarmUp, precompile fact, and pricing order.");
        }
    }

    private static void ValidateWarmSchedule(CompilationUnitSyntax root)
    {
        StructDeclarationSyntax dynamicMode = FindUniqueType(root, "DynamicStorageMode", genericParameterCount: 0);
        StructDeclarationSyntax specializedMode = FindUniqueType(root, "StorageMode", genericParameterCount: 2);
        StructDeclarationSyntax storageWriteMode = FindUniqueType(root, "StorageMode", genericParameterCount: 1);
        ValidateWarmScheduleMethod(dynamicMode, "spec.IsEip8038Enabled?Eip8038Constants.WarmAccess:GasCostOf.WarmStateRead");
        ValidateWarmScheduleMethod(specializedMode, "Eip8038.IsActive?Eip8038Constants.WarmAccess:GasCostOf.WarmStateRead");
        ValidateWarmScheduleMethod(storageWriteMode, "Eip8038.IsActive?Eip8038Constants.WarmAccess:GasCostOf.WarmStateRead");
    }

    private static void ValidateWarmScheduleMethod(StructDeclarationSyntax type, string expectedExpression)
    {
        MethodDeclarationSyntax method = FindUniqueMethod(type, "WarmAccountAccessGas");
        if (!method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            Canonical(method.ReturnType) != "ulong" ||
            method.ParameterList.Parameters is not [{ Identifier.ValueText: "spec" } parameter] ||
            parameter.Type is null || Canonical(parameter.Type) != "IReleaseSpec" ||
            method.ExpressionBody is null ||
            WithoutTrivia(method.ExpressionBody.Expression) != expectedExpression)
        {
            throw new ExtractionException("Account-access warm schedule selector no longer distinguishes EIP-8038 from legacy pricing.");
        }
    }

    private static void ValidateCallSpecDelegation(MethodDeclarationSyntax method)
    {
        if (!method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword)) ||
            !method.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            Canonical(method.ReturnType) != "bool" ||
            method.TypeParameterList?.Parameters.Select(static parameter => parameter.Identifier.ValueText)
                .SequenceEqual(["TGasPolicy"], StringComparer.Ordinal) != true ||
            method.ParameterList.Parameters.Count != 5 ||
            method.ExpressionBody is null ||
            WithoutTrivia(method.ExpressionBody.Expression) !=
            "TGasPolicy.TryConsumeDelegatedAccountAccessGas<Eip2929,Eip8038>(refgas,spec,intracker,tracing,delegated)")
        {
            throw new ExtractionException("CallSpec must route CALL-family delegation through the specialized policy helper.");
        }

        ValidateParameter(method.ParameterList.Parameters[0], "gas", "TGasPolicy", "ref");
        ValidateParameter(method.ParameterList.Parameters[1], "spec", "IReleaseSpec");
        ValidateParameter(method.ParameterList.Parameters[2], "tracker", "StackAccessTracker", "refreadonly");
        ValidateParameter(method.ParameterList.Parameters[3], "tracing", "bool");
        ValidateParameter(method.ParameterList.Parameters[4], "delegated", "Address?");
    }

    private static void ValidateCallSite(MethodDeclarationSyntax method)
    {
        InvocationExpressionSyntax[] delegationCalls = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) ==
                "TSpec.TryConsumeDelegatedAccountAccessGas<TGasPolicy>")
            .ToArray();
        InvocationExpressionSyntax[] accountAccessCalls = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation.Expression) ==
                "TSpec.TryConsumeAccountAccessGas<TGasPolicy>")
            .ToArray();
        InvocationExpressionSyntax codeInfo = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => Canonical(invocation.Expression) == "vm.CodeInfoRepository.GetCachedCodeInfo")
            ?? throw new ExtractionException("CALL-family source no longer loads delegation between the two account-access charges.");
        LocalDeclarationStatementSyntax chargesNewAccount = method.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .SingleOrDefault(statement => statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "chargesNewAccount"))
            ?? throw new ExtractionException("CALL-family source no longer retains the post-access new-account decision.");
        if (accountAccessCalls.Length != 1 || delegationCalls.Length != 1 ||
            Canonical(accountAccessCalls[0]) !=
            "TSpec.TryConsumeAccountAccessGas<TGasPolicy>(refgas,vm.Spec,invm.VmState.AccessTracker,vm.IsTracingAccess,codeSource)" ||
            Canonical(delegationCalls[0]) !=
            "TSpec.TryConsumeDelegatedAccountAccessGas<TGasPolicy>(refgas,vm.Spec,invm.VmState.AccessTracker,vm.IsTracingAccess,delegated)" ||
            accountAccessCalls[0].SpanStart >= codeInfo.SpanStart ||
            codeInfo.SpanStart >= delegationCalls[0].SpanStart ||
            delegationCalls[0].SpanStart >= chargesNewAccount.SpanStart)
        {
            throw new ExtractionException("CALL-family source must retain the ordered code and delegated account-access helper charges before state creation pricing.");
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
            throw new ExtractionException($"Account-access adapter parameter '{name}' does not retain its pinned shape.");
        }
    }

    private static void ValidateReturnGate(IfStatementSyntax statement, string condition)
    {
        if (WithoutTrivia(statement.Condition) != condition || statement.Else is not null ||
            statement.Statement is not ReturnStatementSyntax { Expression: LiteralExpressionSyntax literal } ||
            !literal.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            throw new ExtractionException("Account-access adapter must retain its no-charge return gate.");
        }
    }

    private static void ValidateLocal(StatementSyntax statement, string type, string name, string initializer)
    {
        if (statement is not LocalDeclarationStatementSyntax local ||
            Canonical(local.Declaration.Type) != type ||
            local.Declaration.Variables is not [{ Identifier.ValueText: var actualName, Initializer.Value: ExpressionSyntax actualInitializer }] ||
            actualName != name || WithoutTrivia(actualInitializer) != initializer)
        {
            throw new ExtractionException($"Account-access adapter local '{name}' does not retain its pinned derived-value shape.");
        }
    }

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));

    private static string WithoutTrivia(SyntaxNode node) => Canonical(node);
}
