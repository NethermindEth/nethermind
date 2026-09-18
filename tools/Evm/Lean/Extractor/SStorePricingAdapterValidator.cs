// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record SStorePricingAdapterShape(
    string Method,
    string ActiveGate,
    string AccessCharge,
    string CurrentRead,
    string PricingDelegation,
    string StateAndExecutionCharge,
    IReadOnlyList<string> PostChargeSteps,
    bool AccessPrecedesSlotRead,
    bool ChangedBranchSuppliesNoOpFact,
    bool RefundHelperReportsEachNonzeroComponent);

internal static class SStorePricingAdapterValidator
{
    private const string MeteredMethodName = "InstructionSStoreMetered";
    private const string ActiveGate = "Eip8038.IsActive&&TEip8037.IsActive";
    private const string AccessCharge = "TGasPolicy.TryConsumeStorageAccessGas<Eip2929,Eip8038>(refgas,invmState.AccessTracker,vm.IsTracingAccess,instorageCell,StorageAccessType.SSTORE,spec)";
    private const string CurrentRead = "vm.WorldState.Get(instorageCell)";
    private const string NoOpCharge = "TGasPolicy.TryConsumeNetMeteredSStoreGas<Eip8038>(refgas,spec)";
    private const string PricingDelegation = "SStorePricingKernel.PriceAfterAccess(input,schedule)";
    private const string StateAndExecutionCharge = "TGasPolicy.TryConsumeStateAndExecutionGas(refgas,pricing.StateGasCharge,pricing.ExecutionWriteGas)";
    private const string ClearRefund = "ApplySStoreRefund(vm,vmState,pricing.StorageClearRefund)";
    private const string ClearReversal = "ApplySStoreRefund(vm,vmState,pricing.StorageClearRefundReversal)";
    private const string StateRefill = "vm.CreditStateGasRefund<TEip8037>(refgas,pricing.StateGasRefund)";
    private const string RestoreRefund = "ApplySStoreRefund(vm,vmState,pricing.RestoreOriginalRefund)";

    public static SStorePricingAdapterShape Validate(string sourcePath)
    {
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(sourcePath),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14),
            sourcePath);
        CompilationUnitSyntax root = syntaxTree.GetCompilationUnitRoot();
        MethodDeclarationSyntax metered = FindUniqueMethod(root, MeteredMethodName);
        ValidateMeteredSignature(metered);
        ValidateAccessBeforeRead(metered);
        IfStatementSyntax activeGate = FindActiveGate(metered);
        ValidateActiveBranch(activeGate);
        ValidateUniquePricingDelegation(metered);
        ValidateRefundHelper(root);

        return new SStorePricingAdapterShape(
            MeteredMethodName,
            ActiveGate,
            AccessCharge,
            CurrentRead,
            PricingDelegation,
            StateAndExecutionCharge,
            [ClearRefund, ClearReversal, StateRefill, RestoreRefund],
            AccessPrecedesSlotRead: true,
            ChangedBranchSuppliesNoOpFact: true,
            RefundHelperReportsEachNonzeroComponent: true);
    }

    private static MethodDeclarationSyntax FindUniqueMethod(CompilationUnitSyntax root, string methodName)
    {
        MethodDeclarationSyntax[] methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == methodName)
            .ToArray();
        return methods.Length == 1
            ? methods[0]
            : throw new ExtractionException($"SSTORE adapter must contain exactly one '{methodName}' method.");
    }

    private static void ValidateMeteredSignature(MethodDeclarationSyntax method)
    {
        string[] expectedTypeParameters =
        [
            "TGasPolicy",
            "TTracingInst",
            "TUseNetGasStipendFix",
            "TEip8037",
            "Eip8038",
            "Eip2929",
        ];
        string[] expectedParameters =
        [
            "refEvmStackstack",
            "refTGasPolicygas",
            "VirtualMachine<TGasPolicy>vm",
        ];
        if (method.Body is null || method.ExpressionBody is not null ||
            Canonical(method.ReturnType) != "EvmExceptionType" ||
            method.TypeParameterList?.Parameters.Select(static parameter => parameter.Identifier.ValueText)
                .SequenceEqual(expectedTypeParameters, StringComparer.Ordinal) != true ||
            !method.ParameterList.Parameters.Select(Canonical).SequenceEqual(expectedParameters, StringComparer.Ordinal))
        {
            throw new ExtractionException("SSTORE adapter metered instruction does not have the pinned generic-body shape.");
        }
    }

    private static void ValidateAccessBeforeRead(MethodDeclarationSyntax method)
    {
        InvocationExpressionSyntax access = FindUniqueInvocation(method, AccessCharge);
        int accessCallCount = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(invocation => Canonical(invocation).Contains("TryConsumeStorageAccessGas", StringComparison.Ordinal));
        if (accessCallCount != 1)
        {
            throw new ExtractionException("SSTORE adapter must contain exactly one storage-access charge call.");
        }

        InvocationExpressionSyntax currentRead = FindUniqueInvocation(method, CurrentRead);
        if (access.SpanStart >= currentRead.SpanStart)
        {
            throw new ExtractionException("SSTORE adapter must charge storage access before reading the current slot.");
        }
    }

    private static IfStatementSyntax FindActiveGate(MethodDeclarationSyntax method)
    {
        IfStatementSyntax[] allGates = method.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .ToArray();
        IfStatementSyntax[] gates = allGates
            .Where(statement => Canonical(statement.Condition) == ActiveGate)
            .ToArray();
        int pairedGateCount = allGates.Count(statement =>
        {
            string condition = Canonical(statement.Condition);
            return condition.Contains("Eip8038.IsActive", StringComparison.Ordinal) &&
                condition.Contains("TEip8037.IsActive", StringComparison.Ordinal);
        });
        if (gates.Length != 1 || pairedGateCount != 1 ||
            method.Body!.Statements.Count(statement => ReferenceEquals(statement, gates[0])) != 1 ||
            gates[0].Else is null)
        {
            throw new ExtractionException("SSTORE adapter must have exactly one top-level EIP-8038-and-EIP-8037 gate with a fallback.");
        }

        return gates[0];
    }

    private static void ValidateUniquePricingDelegation(MethodDeclarationSyntax method)
    {
        InvocationExpressionSyntax[] pricingCalls = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation).Contains("SStorePricingKernel.", StringComparison.Ordinal))
            .ToArray();
        if (pricingCalls.Length != 1 || Canonical(pricingCalls[0]) != PricingDelegation)
        {
            throw new ExtractionException("SSTORE adapter must delegate only once to PriceAfterAccess, not the reference Price wrapper.");
        }
    }

    private static void ValidateActiveBranch(IfStatementSyntax activeGate)
    {
        if (activeGate.Statement is not BlockSyntax { Statements.Count: 1 } activeBlock ||
            activeBlock.Statements[0] is not IfStatementSyntax changedBranch ||
            Canonical(changedBranch.Condition) != "newSameAsCurrent" ||
            changedBranch.Else?.Statement is not BlockSyntax changedBlock)
        {
            throw new ExtractionException("SSTORE adapter active gate must split the no-op and changed-value paths exactly once.");
        }

        ValidateNoOpBranch(changedBranch.Statement);
        ValidateChangedBranch(changedBlock);
    }

    private static void ValidateNoOpBranch(StatementSyntax statement)
    {
        if (statement is not BlockSyntax { Statements.Count: 1 } block ||
            block.Statements[0] is not IfStatementSyntax guard)
        {
            throw new ExtractionException("SSTORE adapter no-op path does not have the pinned net-metered charge guard.");
        }

        ValidateOutOfGasGuard(guard, NoOpCharge);
    }

    private static void ValidateChangedBranch(BlockSyntax branch)
    {
        if (branch.Statements.Count != 10)
        {
            throw new ExtractionException("SSTORE adapter changed-value path does not have the pinned ten-step delegation shape.");
        }

        ValidateLocal(branch.Statements[0], "ReadOnlySpan<byte>", "originalValue", "vm.WorldState.GetOriginal(instorageCell)");
        ValidateLocal(branch.Statements[1], "bool", "originalIsZero", "originalValue.IsZero()");
        ValidateLocal(
            branch.Statements[2],
            "SStorePricingInput",
            "input",
            "new(originalIsZero,currentIsZero,newIsZero,Bytes.AreEqual(originalValue,currentValue),false,Bytes.AreEqual(originalValue,bytes))");
        ValidateLocal(
            branch.Statements[3],
            "SStorePostAccessPricingSchedule",
            "schedule",
            "new(Eip8038Constants.StorageWrite,sClearRefunds,TGasPolicy.GetStorageSetStateCost())");
        ValidateLocal(
            branch.Statements[4],
            "SStorePostAccessPricingResult",
            "pricing",
            PricingDelegation);
        if (branch.Statements[5] is not IfStatementSyntax chargeGuard)
        {
            throw new ExtractionException("SSTORE adapter changed-value path must charge execution before applying state/refund effects.");
        }

        ValidateOutOfGasGuard(chargeGuard, StateAndExecutionCharge);
        ValidateExpression(branch.Statements[6], ClearRefund);
        ValidateExpression(branch.Statements[7], ClearReversal);
        ValidateStateRefill(branch.Statements[8]);
        ValidateExpression(branch.Statements[9], RestoreRefund);
    }

    private static void ValidateLocal(StatementSyntax statement, string type, string name, string initializer)
    {
        if (statement is not LocalDeclarationStatementSyntax local ||
            Canonical(local.Declaration.Type) != type ||
            local.Declaration.Variables.Count != 1 ||
            local.Declaration.Variables[0].Identifier.ValueText != name ||
            local.Declaration.Variables[0].Initializer is not { } value ||
            Canonical(value.Value) != initializer)
        {
            throw new ExtractionException($"SSTORE adapter local '{name}' does not have the pinned derived-value shape.");
        }
    }

    private static void ValidateOutOfGasGuard(IfStatementSyntax guard, string invocation)
    {
        if (guard.Else is not null || guard.Statement is not GotoStatementSyntax { Expression: IdentifierNameSyntax label } ||
            label.Identifier.ValueText != "OutOfGas" || guard.Condition is not PrefixUnaryExpressionSyntax
            {
                OperatorToken: { RawKind: (int)SyntaxKind.ExclamationToken },
                Operand: InvocationExpressionSyntax invoked,
            } || Canonical(invoked) != invocation)
        {
            throw new ExtractionException("SSTORE adapter gas guard does not preserve the pinned OutOfGas branch and charge order.");
        }
    }

    private static void ValidateExpression(StatementSyntax statement, string expected)
    {
        if (statement is not ExpressionStatementSyntax expression || Canonical(expression.Expression) != expected)
        {
            throw new ExtractionException($"SSTORE adapter post-charge step '{expected}' is missing or reordered.");
        }
    }

    private static void ValidateStateRefill(StatementSyntax statement)
    {
        if (statement is not IfStatementSyntax
            {
                Else: null,
                Condition: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression } condition,
                Statement: ExpressionStatementSyntax expression,
            } || Canonical(condition) != "pricing.StateGasRefund!=0" || Canonical(expression.Expression) != StateRefill)
        {
            throw new ExtractionException("SSTORE adapter must refill state gas between the clear reversal and restore refund components.");
        }
    }

    private static void ValidateRefundHelper(CompilationUnitSyntax root)
    {
        MethodDeclarationSyntax helper = FindUniqueMethod(root, "ApplySStoreRefund");
        if (!helper.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PrivateKeyword)) ||
            !helper.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.StaticKeyword)) ||
            Canonical(helper.ReturnType) != "void" ||
            helper.TypeParameterList?.Parameters.Select(static parameter => parameter.Identifier.ValueText)
                .SequenceEqual(["TGasPolicy"], StringComparer.Ordinal) != true ||
            !helper.ParameterList.Parameters.Select(Canonical).SequenceEqual(
                ["VirtualMachine<TGasPolicy>vm", "VmState<TGasPolicy>vmState", "longrefund"],
                StringComparer.Ordinal) ||
            helper.Body is not { Statements.Count: 3 } body ||
            body.Statements[0] is not IfStatementSyntax
            {
                Else: null,
                Condition: BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } zeroGuard,
                Statement: ReturnStatementSyntax { Expression: null },
            } || Canonical(zeroGuard) != "refund==0" ||
            body.Statements[1] is not ExpressionStatementSyntax refundUpdate ||
            Canonical(refundUpdate.Expression) != "vmState.Refund=unchecked(vmState.Refund+refund)")
        {
            throw new ExtractionException("SSTORE adapter refund helper does not preserve the pinned nonzero signed refund update.");
        }

        IfStatementSyntax[] tracerGuards = helper.Body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition) == "vm.IsTracingRefunds")
            .ToArray();
        if (tracerGuards.Length != 1 ||
            tracerGuards[0].Statement is not ExpressionStatementSyntax tracer ||
            Canonical(tracer.Expression) != "vm.TxTracer.ReportRefund(refund)")
        {
            throw new ExtractionException("SSTORE adapter refund helper must emit one tracer refund event for each nonzero component.");
        }
    }

    private static InvocationExpressionSyntax FindUniqueInvocation(MethodDeclarationSyntax method, string expected)
    {
        InvocationExpressionSyntax[] matches = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => Canonical(invocation) == expected)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new ExtractionException($"SSTORE adapter must contain exactly one '{expected}' invocation.");
    }

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.WithoutTrivia().ToFullString().Where(static character => !char.IsWhiteSpace(character)));
}
