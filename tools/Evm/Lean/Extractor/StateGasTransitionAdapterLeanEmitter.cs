// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.Extractor;

internal sealed record StateGasTransitionAdapterKernelModel(
    IReadOnlyList<string> OutcomeCases,
    IReadOnlyList<StateGasTransitionAdapterFunction> Functions,
    IReadOnlyList<StateGasTransitionAdapterProductionDelegation> ProductionDelegations);

internal sealed record StateGasTransitionAdapterFunction(
    string Name,
    string Signature,
    string CoreFunction,
    string OutcomeKind,
    bool ClassifiesNegativeAmount,
    IReadOnlyList<StateGasTransitionAdapterParameter> Parameters);

internal sealed record StateGasTransitionAdapterParameter(int Ordinal, string Name, string Type);

internal sealed record StateGasTransitionAdapterProductionDelegation(
    string Name,
    int ParameterCount,
    string ReturnKind,
    bool AppliesTransition,
    bool ReturnsUnappliedAmount,
    bool PreservesNegativeAmountException);

internal static class StateGasTransitionAdapterLeanEmitter
{
    private const string AdapterKernelTypeName = "Nethermind.Evm.GasPolicy.StateGasTransitionAdapterKernel";
    private const string OutcomeTypeName = "Nethermind.Evm.GasPolicy.StateGasTransitionAdapterOutcome";
    private const string OutcomeKindTypeName = "Nethermind.Evm.GasPolicy.StateGasTransitionAdapterOutcomeKind";
    private const string TransitionKernelTypeName = "Nethermind.Evm.GasPolicy.StateGasTransitionKernel";
    private const string TransitionResultTypeName = "Nethermind.Evm.GasPolicy.StateGasTransitionResult";
    private const string ProductionPolicyTypeName = "EthereumGasPolicy";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private static readonly MethodShape[] RootShapes =
    [
        new("AddStateGasRefundToReservoir", "ulong", "long", "long", "long", "long", "long", "bool"),
        new("DiscardStateGas", "ulong", "long", "long", "long", "long", "long", "long"),
        new("Refund", "ulong", "long", "long", "long", "long", "ulong", "long", "long", "long", "long"),
        new("RefundStateGas", "ulong", "long", "long", "long", "long", "long", "long", "bool"),
        new("RemoveStateGasRefundFromReservoir", "ulong", "long", "long", "long", "long", "long"),
        new("RepayStateGasSpill", "ulong", "long", "long", "long", "long"),
        new("RestoreChildStateGas", "ulong", "long", "long", "long", "long", "long", "long", "long", "long"),
        new("RestoreChildStateGasOnHalt", "ulong", "long", "long", "long", "long", "long", "long", "long", "long"),
        new("RevertRefundToHalt", "ulong", "long", "long", "long", "long", "long", "long", "long"),
    ];

    private static readonly ProductionShape[] ProductionShapes =
    [
        new("Refund", 2, "void", "gas", false, false,
            ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded", "childGas.Value", "childGas.StateReservoir", "childGas.StateGasUsed", "childGas.StateGasSpill", "childGas.StateGasSpillRefunded"]),
        new("RepayStateGasSpill", 1, "void", "gas", false, false,
            ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded"]),
        new("RestoreChildStateGas", 2, "void", "parentGas", false, false,
            ["parentGas.Value", "parentGas.StateReservoir", "parentGas.StateGasUsed", "parentGas.StateGasSpill", "parentGas.StateGasSpillRefunded", "childGas.StateReservoir", "childGas.StateGasUsed", "childGas.StateGasSpill", "childGas.StateGasSpillRefunded"]),
        new("RestoreChildStateGasOnHalt", 2, "void", "parentGas", false, false,
            ["parentGas.Value", "parentGas.StateReservoir", "parentGas.StateGasUsed", "parentGas.StateGasSpill", "parentGas.StateGasSpillRefunded", "childGas.StateReservoir", "childGas.StateGasUsed", "childGas.StateGasSpill", "childGas.StateGasSpillRefunded"]),
        new("RevertRefundToHalt", 2, "void", "parentGas", false, false,
            ["parentGas.Value", "parentGas.StateReservoir", "parentGas.StateGasUsed", "parentGas.StateGasSpill", "parentGas.StateGasSpillRefunded", "childGas.StateGasUsed", "childGas.StateGasSpill", "childGas.StateGasSpillRefunded"]),
        new("RefundStateGas", 4, "void", "gas", false, false,
            ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded", "amount", "stateGasFloor", "trackSpillRefund"]),
        new("DiscardStateGas", 3, "long", "gas", true, false,
            ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded", "amount", "stateGasFloor"]),
        new("AddStateGasRefundToReservoir", 3, "void", "gas", false, false,
            ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded", "amount", "trackSpillRefund"]),
        new("RemoveStateGasRefundFromReservoir", 2, "void", "gas", false, true,
            ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded", "amount"]),
    ];

    public static void ValidateRoots(IReadOnlyList<IMethodSymbol> roots)
    {
        if (roots.Count != RootShapes.Length)
        {
            throw new ExtractionException("State-gas transition adapter Lean emission requires exactly nine roots.");
        }

        IMethodSymbol[] ordered = roots.OrderBy(static method => method.Name, StringComparer.Ordinal).ToArray();
        for (int index = 0; index < RootShapes.Length; index++)
        {
            MethodShape shape = RootShapes[index];
            IMethodSymbol method = ordered[index];
            if (method.ContainingType.ToDisplayString() != AdapterKernelTypeName ||
                method.Name != shape.Name ||
                method.DeclaredAccessibility != Accessibility.Public ||
                !method.IsStatic ||
                method.ReturnType.ToDisplayString() != OutcomeTypeName ||
                method.Parameters.Length != shape.ParameterTypes.Length ||
                method.Parameters.Where((parameter, parameterIndex) =>
                    parameter.Type.ToDisplayString() != shape.ParameterTypes[parameterIndex]).Any())
            {
                throw new ExtractionException($"State-gas transition adapter root '{method}' does not match its pinned signature.");
            }
        }
    }

    public static StateGasTransitionAdapterKernelModel Normalize(
        SemanticModel semanticModel,
        IReadOnlyList<IMethodSymbol> roots,
        IReadOnlyList<IMethodSymbol> extractedMethods,
        string ethereumGasPolicySourcePath)
    {
        ValidateRoots(roots);
        if (extractedMethods.Count != RootShapes.Length ||
            !extractedMethods.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal).SequenceEqual(
                roots.Select(StateGasChargeExtractor.Display).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new ExtractionException("State-gas transition adapter extraction did not retain exactly the pinned root graph.");
        }

        INamedTypeSymbol outcomeType = semanticModel.Compilation.GetTypeByMetadataName(OutcomeTypeName)
            ?? throw new ExtractionException($"Required adapter outcome type '{OutcomeTypeName}' was not found.");
        INamedTypeSymbol outcomeKindType = semanticModel.Compilation.GetTypeByMetadataName(OutcomeKindTypeName)
            ?? throw new ExtractionException($"Required adapter outcome kind '{OutcomeKindTypeName}' was not found.");
        ValidateOutcome(outcomeType);
        IReadOnlyList<string> outcomeCases = NormalizeOutcomeCases(outcomeKindType);
        ModelBuilder builder = new(semanticModel);
        StateGasTransitionAdapterFunction[] functions = roots
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .Select(builder.Normalize)
            .ToArray();
        IReadOnlyList<StateGasTransitionAdapterProductionDelegation> productionDelegations =
            ValidateProductionDelegation(ethereumGasPolicySourcePath);
        return new StateGasTransitionAdapterKernelModel(outcomeCases, functions, productionDelegations);
    }

    public static byte[] Emit(
        StateGasTransitionAdapterKernelModel model,
        string extractorVersion,
        string compilerVersion,
        string sourcePath,
        string sourceHash,
        string irHash)
    {
        ValidateModel(model);
        StringBuilder functions = new();
        foreach (StateGasTransitionAdapterFunction function in model.Functions)
        {
            AppendFunction(functions, function);
            functions.AppendLine();
        }

        string source = $$"""
            -- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
            -- SPDX-License-Identifier: LGPL-3.0-only

            -- This file is generated. Do not edit.
            -- Extractor version: {{extractorVersion}}
            -- Roslyn compiler version: {{compilerVersion}}
            -- Production source: {{sourcePath}}
            -- Production source SHA-256: {{sourceHash}}
            -- Canonical state-gas-transition-adapter IR SHA-256: {{irHash}}

            import Eip803x.Generated.StateGasTransitionKernel

            namespace Eip803x.Generated.StateGasTransitionAdapterKernel

            open Eip803x.Generated.StateGasTransitionKernel

            inductive OutcomeKind where
            {{RenderOutcomeCases(model.OutcomeCases)}}
              deriving DecidableEq, Repr

            structure Outcome where
              kind : OutcomeKind
              transition : StateGasTransitionResult
              deriving DecidableEq, Repr

            def unchanged
                (value : Nat)
                (stateReservoir : Int)
                (stateGasUsed : Int)
                (stateGasSpill : Int)
                (stateGasSpillRefunded : Int) : StateGasTransitionResult :=
              { value := normalizeUInt64 value
                stateReservoir := wrapInt64 stateReservoir
                stateGasUsed := wrapInt64 stateGasUsed
                stateGasSpill := wrapInt64 stateGasSpill
                stateGasSpillRefunded := wrapInt64 stateGasSpillRefunded
                unappliedAmount := 0 }

            {{functions.ToString().TrimEnd()}}

            end Eip803x.Generated.StateGasTransitionAdapterKernel
            """;
        return Utf8WithoutBom.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static void ValidateOutcome(INamedTypeSymbol outcomeType)
    {
        if (!outcomeType.IsReadOnly ||
            outcomeType.DeclaringSyntaxReferences is not [SyntaxReference reference] ||
            reference.GetSyntax() is not StructDeclarationSyntax declaration ||
            declaration.AttributeLists.Count != 0 ||
            declaration.ParameterList?.Parameters.Count != 2 ||
            declaration.ParameterList.Parameters[0].Identifier.ValueText != "kind" ||
            declaration.ParameterList.Parameters[1].Identifier.ValueText != "transition" ||
            declaration.Members.Count != 2 ||
            declaration.Members.Any(static member => member is not FieldDeclarationSyntax))
        {
            throw new ExtractionException("State-gas transition adapter outcome does not match the pinned data shape.");
        }

        IFieldSymbol[] fields = outcomeType.GetMembers().OfType<IFieldSymbol>()
            .Where(static field => !field.IsImplicitlyDeclared)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        if (fields.Length != 2 ||
            fields[0].Name != "Kind" || fields[0].Type.ToDisplayString() != OutcomeKindTypeName ||
            fields[1].Name != "Transition" || fields[1].Type.ToDisplayString() != TransitionResultTypeName ||
            fields.Any(static field => !field.IsReadOnly || field.DeclaredAccessibility != Accessibility.Public))
        {
            throw new ExtractionException("State-gas transition adapter outcome fields do not match the pinned data shape.");
        }
    }

    private static IReadOnlyList<string> NormalizeOutcomeCases(INamedTypeSymbol outcomeKindType)
    {
        if (outcomeKindType.TypeKind != TypeKind.Enum ||
            outcomeKindType.EnumUnderlyingType?.SpecialType != SpecialType.System_Byte)
        {
            throw new ExtractionException("State-gas transition adapter outcome kind must be a byte enum.");
        }

        IFieldSymbol[] values = outcomeKindType.GetMembers().OfType<IFieldSymbol>()
            .Where(static field => field.HasConstantValue)
            .OrderBy(static field => field.Locations[0].SourceSpan.Start)
            .ToArray();
        string[] expected = ["CompletedVoid", "CompletedDiscard", "ArgumentException"];
        if (values.Length != expected.Length ||
            values.Where((value, index) => value.Name != expected[index] || Convert.ToByte(value.ConstantValue) != index).Any())
        {
            throw new ExtractionException("State-gas transition adapter outcome kind does not match the pinned values.");
        }

        return expected.Select(LowerFirst).ToArray();
    }

    private static IReadOnlyList<StateGasTransitionAdapterProductionDelegation> ValidateProductionDelegation(
        string ethereumGasPolicySourcePath)
    {
        if (!File.Exists(ethereumGasPolicySourcePath))
        {
            throw new ExtractionException($"Pinned production adapter source was not found: {ethereumGasPolicySourcePath}");
        }

        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(ethereumGasPolicySourcePath),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp14),
            ethereumGasPolicySourcePath);
        Diagnostic[] diagnostics = syntaxTree.GetDiagnostics().Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (diagnostics.Length != 0)
        {
            throw new ExtractionException($"Pinned production adapter source does not parse: {diagnostics[0].GetMessage()}");
        }

        StructDeclarationSyntax policy = syntaxTree.GetRoot().DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .SingleOrDefault(static declaration => declaration.Identifier.ValueText == ProductionPolicyTypeName)
            ?? throw new ExtractionException($"Production adapter type '{ProductionPolicyTypeName}' was not found.");
        List<StateGasTransitionAdapterProductionDelegation> delegations = [];
        foreach (ProductionShape shape in ProductionShapes)
        {
            MethodDeclarationSyntax method = policy.Members.OfType<MethodDeclarationSyntax>()
                .SingleOrDefault(candidate => candidate.Identifier.ValueText == shape.Name &&
                    candidate.ParameterList.Parameters.Count == shape.ParameterCount)
                ?? throw new ExtractionException($"Production adapter '{shape.Name}' does not match the pinned overload.");
            ValidateProductionMethod(method, shape);
            delegations.Add(new StateGasTransitionAdapterProductionDelegation(
                shape.Name,
                shape.ParameterCount,
                shape.ReturnKind,
                true,
                shape.ReturnsUnappliedAmount,
                shape.PreservesNegativeAmountException));
        }

        ValidateRefundStateGasConvenienceOverload(policy);
        return delegations.OrderBy(static delegation => delegation.Name, StringComparer.Ordinal)
            .ThenBy(static delegation => delegation.ParameterCount)
            .ToArray();
    }

    private static void ValidateProductionMethod(MethodDeclarationSyntax method, ProductionShape shape)
    {
        if (method.Body is null || method.ExpressionBody is not null ||
            method.ReturnType.ToString() != shape.ReturnKind ||
            !method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
            !method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            ContainsGasFieldAssignment(method))
        {
            throw new ExtractionException($"Production adapter '{shape.Name}' is outside the pinned value-boundary shape.");
        }

        InvocationExpressionSyntax[] adapterCalls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => IsMemberCall(invocation, "StateGasTransitionAdapterKernel", shape.Name))
            .ToArray();
        if (adapterCalls.Length != 1 ||
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(static invocation => IsMemberCall(invocation, "StateGasTransitionKernel", null)))
        {
            throw new ExtractionException($"Production adapter '{shape.Name}' does not delegate exactly once through the pure boundary.");
        }

        InvocationExpressionSyntax adapterCall = adapterCalls[0];
        if (adapterCall.ArgumentList.Arguments.Count != shape.Arguments.Count ||
            adapterCall.ArgumentList.Arguments.Where((argument, index) =>
                NormalizeText(argument.Expression) != shape.Arguments[index] ||
                !argument.RefKindKeyword.IsKind(SyntaxKind.None)).Any())
        {
            throw new ExtractionException($"Production adapter '{shape.Name}' does not preserve the pinned field projection.");
        }

        LocalDeclarationStatementSyntax outcome = method.Body.Statements.OfType<LocalDeclarationStatementSyntax>()
            .SingleOrDefault(statement => statement.Declaration.Variables.Count == 1 &&
                NormalizeText(statement.Declaration.Type) == "StateGasTransitionAdapterOutcome" &&
                statement.Declaration.Variables[0].Identifier.ValueText == "outcome" &&
                statement.Declaration.Variables[0].Initializer?.Value.Span == adapterCall.Span)
            ?? throw new ExtractionException($"Production adapter '{shape.Name}' must bind exactly the pure outcome.");
        InvocationExpressionSyntax apply = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(invocation => IsMemberCall(invocation, "", "ApplyStateGasTransition"))
            ?? throw new ExtractionException($"Production adapter '{shape.Name}' must apply the pure outcome.");
        ValidateApply(apply, shape.GasParameter);
        if (apply.SpanStart <= outcome.SpanStart)
        {
            throw new ExtractionException($"Production adapter '{shape.Name}' applies state before obtaining the pure outcome.");
        }

        if (shape.ReturnsUnappliedAmount)
        {
            ReturnStatementSyntax returnStatement = method.Body.Statements.OfType<ReturnStatementSyntax>()
                .SingleOrDefault(statement => statement.Expression is not null &&
                    NormalizeText(statement.Expression) == "outcome.Transition.UnappliedAmount")
                ?? throw new ExtractionException("DiscardStateGas must return the pure outcome's UnappliedAmount.");
            if (returnStatement.SpanStart <= apply.SpanStart)
            {
                throw new ExtractionException("DiscardStateGas must apply state before returning UnappliedAmount.");
            }
        }

        if (shape.PreservesNegativeAmountException)
        {
            ValidateNegativeAmountException(method, outcome, apply);
        }

        ValidateProductionStatementShape(method, shape, outcome, apply);
    }

    private static void ValidateProductionStatementShape(
        MethodDeclarationSyntax method,
        ProductionShape shape,
        LocalDeclarationStatementSyntax outcome,
        InvocationExpressionSyntax apply)
    {
        SyntaxList<StatementSyntax> statements = method.Body!.Statements;
        int outcomeIndex = shape.Name == "RefundStateGas" ? 1 : 0;
        int expectedStatementCount = shape.ReturnsUnappliedAmount || shape.PreservesNegativeAmountException ||
            shape.Name == "RefundStateGas"
            ? 3
            : 2;
        if (statements.Count != expectedStatementCount || statements[outcomeIndex].Span != outcome.Span ||
            apply.Parent is not ExpressionStatementSyntax applyStatement ||
            (shape.PreservesNegativeAmountException
                ? statements[2].Span != applyStatement.Span
                : statements[outcomeIndex + 1].Span != applyStatement.Span))
        {
            throw new ExtractionException($"Production adapter '{shape.Name}' contains statements outside the pinned value-boundary shape.");
        }

        if (shape.Name == "RefundStateGas" &&
            (statements[0] is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax assertion } ||
             !IsMemberCall(assertion, "Debug", "Assert") || assertion.ArgumentList.Arguments.Count != 2 ||
             NormalizeText(assertion.ArgumentList.Arguments[0].Expression) != "amount>=0"))
        {
            throw new ExtractionException("RefundStateGas must retain its pinned negative-amount assertion.");
        }

        if (shape.ReturnsUnappliedAmount &&
            statements[2] is not ReturnStatementSyntax { Expression: not null })
        {
            throw new ExtractionException("DiscardStateGas must retain its pinned return statement.");
        }

        if (shape.PreservesNegativeAmountException &&
            statements[1] is not IfStatementSyntax)
        {
            throw new ExtractionException("RemoveStateGasRefundFromReservoir must retain its pinned exception branch.");
        }
    }

    private static void ValidateApply(InvocationExpressionSyntax apply, string gasParameter)
    {
        if (apply.ArgumentList.Arguments.Count != 2 ||
            !apply.ArgumentList.Arguments[0].RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
            NormalizeText(apply.ArgumentList.Arguments[0].Expression) != gasParameter ||
            !apply.ArgumentList.Arguments[1].RefKindKeyword.IsKind(SyntaxKind.InKeyword) ||
            NormalizeText(apply.ArgumentList.Arguments[1].Expression) != "outcome")
        {
            throw new ExtractionException("Production adapter ApplyStateGasTransition call does not match the pinned projection.");
        }
    }

    private static void ValidateNegativeAmountException(
        MethodDeclarationSyntax method,
        LocalDeclarationStatementSyntax outcome,
        InvocationExpressionSyntax apply)
    {
        IfStatementSyntax branch = method.Body!.Statements.OfType<IfStatementSyntax>().SingleOrDefault()
            ?? throw new ExtractionException("RemoveStateGasRefundFromReservoir must classify the negative outcome.");
        if (branch.SpanStart <= outcome.SpanStart || branch.SpanStart >= apply.SpanStart ||
            NormalizeText(branch.Condition) != "outcome.KindisStateGasTransitionAdapterOutcomeKind.ArgumentException" ||
            branch.Statement is not BlockSyntax block || block.Statements.Count != 2 ||
            block.Statements[0] is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment } ||
            NormalizeText(assignment.Left) != "_" ||
            assignment.Right is not InvocationExpressionSyntax clamp ||
            !IsMemberCall(clamp, "Math", "Clamp") ||
            clamp.ArgumentList.Arguments.Count != 3 ||
            NormalizeText(clamp.ArgumentList.Arguments[0].Expression) != "gas.StateReservoir" ||
            NormalizeText(clamp.ArgumentList.Arguments[1].Expression) != "0" ||
            NormalizeText(clamp.ArgumentList.Arguments[2].Expression) != "amount" ||
            block.Statements[1] is not ReturnStatementSyntax { Expression: null })
        {
            throw new ExtractionException("RemoveStateGasRefundFromReservoir must preserve the pre-mutation ArgumentException path.");
        }
    }

    private static void ValidateRefundStateGasConvenienceOverload(StructDeclarationSyntax policy)
    {
        MethodDeclarationSyntax method = policy.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(candidate => candidate.Identifier.ValueText == "RefundStateGas" &&
                candidate.ParameterList.Parameters.Count == 3)
            ?? throw new ExtractionException("RefundStateGas convenience overload was not found.");
        if (method.Body is not null || method.ExpressionBody?.Expression is not InvocationExpressionSyntax call ||
            !IsMemberCall(call, "", "RefundStateGas") || call.ArgumentList.Arguments.Count != 4 ||
            !call.ArgumentList.Arguments[0].RefKindKeyword.IsKind(SyntaxKind.RefKeyword) ||
            NormalizeText(call.ArgumentList.Arguments[0].Expression) != "gas" ||
            NormalizeText(call.ArgumentList.Arguments[1].Expression) != "amount" ||
            NormalizeText(call.ArgumentList.Arguments[2].Expression) != "stateGasFloor" ||
            call.ArgumentList.Arguments[3].NameColon?.Name.Identifier.ValueText != "trackSpillRefund" ||
            !call.ArgumentList.Arguments[3].Expression.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            throw new ExtractionException("RefundStateGas convenience overload does not retain the pinned tracked-refund delegation.");
        }
    }

    private static bool ContainsGasFieldAssignment(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(static assignment =>
            assignment.Left is MemberAccessExpressionSyntax member &&
            member.Name.Identifier.ValueText is "Value" or "StateReservoir" or "StateGasUsed" or "StateGasSpill" or "StateGasSpillRefunded");

    private static bool IsMemberCall(InvocationExpressionSyntax invocation, string receiver, string? name) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax member =>
                (receiver.Length == 0 || NormalizeText(member.Expression) == receiver) &&
                (name is null || member.Name.Identifier.ValueText == name),
            IdentifierNameSyntax identifier =>
                receiver.Length == 0 && name is not null && identifier.Identifier.ValueText == name,
            _ => false,
        };

    private static string NormalizeText(SyntaxNode node) =>
        string.Concat(node.DescendantTokens().Select(static token => token.Text));

    private static void ValidateModel(StateGasTransitionAdapterKernelModel model)
    {
        string[] expectedOutcomes = ["completedVoid", "completedDiscard", "argumentException"];
        if (!model.OutcomeCases.SequenceEqual(expectedOutcomes, StringComparer.Ordinal) ||
            model.Functions.Count != RootShapes.Length ||
            model.ProductionDelegations.Count != ProductionShapes.Length)
        {
            throw new ExtractionException("State-gas transition adapter model does not match the pinned shape.");
        }

        foreach (MethodShape shape in RootShapes)
        {
            StateGasTransitionAdapterFunction function = model.Functions.SingleOrDefault(
                candidate => candidate.Name == shape.Name)
                ?? throw new ExtractionException($"State-gas transition adapter model omits '{shape.Name}'.");
            string expectedOutcomeKind = shape.Name == "DiscardStateGas" ? "completedDiscard" : "completedVoid";
            bool expectedNegativeClassification = shape.Name == "RemoveStateGasRefundFromReservoir";
            if (function.CoreFunction != LowerFirst(shape.Name) ||
                function.Parameters.Count != shape.ParameterTypes.Length ||
                function.Parameters.Where((parameter, index) => parameter.Type != shape.ParameterTypes[index]).Any() ||
                function.OutcomeKind != expectedOutcomeKind ||
                function.ClassifiesNegativeAmount != expectedNegativeClassification)
            {
                throw new ExtractionException($"State-gas transition adapter model for '{shape.Name}' is not pinned.");
            }
        }
    }

    private static void AppendFunction(StringBuilder builder, StateGasTransitionAdapterFunction function)
    {
        builder.Append("def ");
        builder.Append(LowerFirst(function.Name));
        builder.AppendLine();
        foreach (StateGasTransitionAdapterParameter parameter in function.Parameters)
        {
            builder.Append("    (");
            builder.Append(parameter.Name);
            builder.Append(" : ");
            builder.Append(LeanType(parameter.Type));
            builder.AppendLine(")");
        }

        builder.AppendLine("    : Outcome :=");
        if (function.ClassifiesNegativeAmount)
        {
            builder.AppendLine("  if amount < 0 then");
            builder.AppendLine("    { kind := .argumentException");
            builder.Append("      transition := unchanged value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded }");
            builder.AppendLine();
            builder.AppendLine("  else");
            builder.AppendLine("    { kind := .completedVoid");
            builder.Append("      transition := ");
            AppendCoreCall(builder, function);
            builder.AppendLine(" }");
            return;
        }

        builder.Append("  { kind := .");
        builder.Append(function.OutcomeKind);
        builder.AppendLine();
        builder.Append("    transition := ");
        AppendCoreCall(builder, function);
        builder.AppendLine(" }");
    }

    private static void AppendCoreCall(StringBuilder builder, StateGasTransitionAdapterFunction function)
    {
        builder.Append("Eip803x.Generated.StateGasTransitionKernel.");
        builder.Append(function.CoreFunction);
        foreach (StateGasTransitionAdapterParameter parameter in function.Parameters)
        {
            builder.Append(' ');
            builder.Append(parameter.Name);
        }
    }

    private static string RenderOutcomeCases(IReadOnlyList<string> outcomes) =>
        string.Join("\n", outcomes.Select(static outcome => "  | " + outcome));

    private static string LeanType(string type) => type switch
    {
        "ulong" => "Nat",
        "long" => "Int",
        "bool" => "Bool",
        _ => throw new ExtractionException($"State-gas transition adapter parameter type '{type}' is not supported."),
    };

    private static string LowerFirst(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private sealed record MethodShape(string Name, params string[] ParameterTypes);

    private sealed record ProductionShape(
        string Name,
        int ParameterCount,
        string ReturnKind,
        string GasParameter,
        bool ReturnsUnappliedAmount,
        bool PreservesNegativeAmountException,
        IReadOnlyList<string> Arguments);

    private sealed class ModelBuilder(SemanticModel semanticModel)
    {
        private readonly SemanticModel _semanticModel = semanticModel;

        public StateGasTransitionAdapterFunction Normalize(IMethodSymbol method)
        {
            MethodDeclarationSyntax declaration = method.DeclaringSyntaxReferences.Single().GetSyntax()
                as MethodDeclarationSyntax
                ?? throw new ExtractionException($"Adapter root '{method}' is not a method declaration.");
            if (declaration.Body is not null || declaration.ExpressionBody?.Expression is not ExpressionSyntax body)
            {
                throw new ExtractionException($"Adapter root '{method}' must use the pinned expression form.");
            }

            string expectedKind = method.Name == "DiscardStateGas" ? "CompletedDiscard" : "CompletedVoid";
            bool classifiesNegativeAmount = method.Name == "RemoveStateGasRefundFromReservoir";
            if (classifiesNegativeAmount)
            {
                ValidateNegativeOutcome(method, body);
            }
            else
            {
                ValidateCompletedOutcome(method, body, expectedKind);
            }

            return new StateGasTransitionAdapterFunction(
                method.Name,
                StateGasChargeExtractor.Display(method),
                LowerFirst(method.Name),
                LowerFirst(expectedKind),
                classifiesNegativeAmount,
                method.Parameters.Select(static parameter => new StateGasTransitionAdapterParameter(
                    parameter.Ordinal,
                    parameter.Name,
                    parameter.Type.ToDisplayString())).ToArray());
        }

        private void ValidateCompletedOutcome(IMethodSymbol method, ExpressionSyntax expression, string expectedKind)
        {
            IObjectCreationOperation outcome = GetObjectCreation(expression, method.Name);
            ValidateOutcomeKind(outcome.Arguments[0].Value, expectedKind, method.Name);
            ValidateCoreCall(method, outcome.Arguments[1].Value);
        }

        private void ValidateNegativeOutcome(IMethodSymbol method, ExpressionSyntax expression)
        {
            IConditionalOperation conditional = _semanticModel.GetOperation(expression) as IConditionalOperation
                ?? throw new ExtractionException("RemoveStateGasRefundFromReservoir must classify its outcome with a conditional expression.");
            if (conditional.Condition is not IBinaryOperation { OperatorKind: BinaryOperatorKind.LessThan } comparison ||
                !IsParameter(comparison.LeftOperand, method.Parameters.Single(static parameter => parameter.Name == "amount")) ||
                !IsNumericZero(comparison.RightOperand))
            {
                throw new ExtractionException("RemoveStateGasRefundFromReservoir must use the pinned amount < 0 classification.");
            }

            IObjectCreationOperation exceptional = GetObjectCreation(conditional.WhenTrue, method.Name);
            ValidateOutcomeKind(exceptional.Arguments[0].Value, "ArgumentException", method.Name);
            IObjectCreationOperation unchanged = GetObjectCreation(exceptional.Arguments[1].Value, method.Name);
            if (unchanged.Type?.ToDisplayString() != TransitionResultTypeName || unchanged.Arguments.Length != 6)
            {
                throw new ExtractionException("RemoveStateGasRefundFromReservoir must preserve the exact unchanged transition result.");
            }

            for (int index = 0; index < 5; index++)
            {
                if (!IsParameter(unchanged.Arguments[index].Value, method.Parameters[index]))
                {
                    throw new ExtractionException("RemoveStateGasRefundFromReservoir exceptional state does not preserve the input fields.");
                }
            }

            if (!IsNumericZero(unchanged.Arguments[5].Value))
            {
                throw new ExtractionException("RemoveStateGasRefundFromReservoir exceptional result must reset UnappliedAmount to zero.");
            }

            IObjectCreationOperation completed = GetObjectCreation(conditional.WhenFalse
                ?? throw new ExtractionException("RemoveStateGasRefundFromReservoir must have a completed outcome."), method.Name);
            ValidateOutcomeKind(completed.Arguments[0].Value, "CompletedVoid", method.Name);
            ValidateCoreCall(method, completed.Arguments[1].Value);
        }

        private void ValidateCoreCall(IMethodSymbol adapterMethod, IOperation operation)
        {
            if (operation is not IInvocationOperation invocation ||
                invocation.TargetMethod.ContainingType.ToDisplayString() != TransitionKernelTypeName ||
                invocation.TargetMethod.Name != adapterMethod.Name ||
                invocation.TargetMethod.ReturnType.ToDisplayString() != TransitionResultTypeName ||
                invocation.Arguments.Length != adapterMethod.Parameters.Length)
            {
                throw new ExtractionException($"Adapter root '{adapterMethod.Name}' does not call its pinned transition kernel method.");
            }

            for (int index = 0; index < invocation.Arguments.Length; index++)
            {
                if (!IsParameter(invocation.Arguments[index].Value, adapterMethod.Parameters[index]))
                {
                    throw new ExtractionException($"Adapter root '{adapterMethod.Name}' does not forward its parameters unchanged.");
                }
            }
        }

        private void ValidateOutcomeKind(IOperation operation, string expected, string methodName)
        {
            if (operation is not IFieldReferenceOperation field ||
                field.Field.ContainingType.ToDisplayString() != OutcomeKindTypeName ||
                field.Field.Name != expected)
            {
                throw new ExtractionException($"Adapter root '{methodName}' does not use the pinned outcome kind '{expected}'.");
            }
        }

        private IObjectCreationOperation GetObjectCreation(IOperation operation, string methodName)
        {
            IOperation unwrapped = operation is IConversionOperation { IsImplicit: true } conversion
                ? conversion.Operand
                : operation;
            return unwrapped as IObjectCreationOperation
                ?? throw new ExtractionException(
                    $"Adapter root '{methodName}' does not construct its pinned outcome ({unwrapped.Kind}).");
        }

        private IObjectCreationOperation GetObjectCreation(ExpressionSyntax expression, string methodName) =>
            GetObjectCreation(_semanticModel.GetOperation(expression)
                ?? throw new ExtractionException($"Roslyn did not bind adapter root '{methodName}'."), methodName);

        private static bool IsParameter(IOperation operation, IParameterSymbol parameter) =>
            operation is IParameterReferenceOperation reference &&
            SymbolEqualityComparer.Default.Equals(reference.Parameter, parameter);

        private static bool IsNumericZero(IOperation operation)
        {
            IOperation value = operation is IConversionOperation { IsImplicit: true } conversion
                ? conversion.Operand
                : operation;
            return value is ILiteralOperation { ConstantValue.HasValue: true } literal &&
                literal.ConstantValue.Value is sbyte or byte or short or ushort or int or uint or long or ulong &&
                Convert.ToInt64(literal.ConstantValue.Value) == 0;
        }
    }
}
