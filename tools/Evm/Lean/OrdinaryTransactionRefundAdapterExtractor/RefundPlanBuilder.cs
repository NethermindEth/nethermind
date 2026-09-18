// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor;

internal static class RefundPlanBuilder
{
    private const string Processor = "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase`1";
    private const string Policy = "Nethermind.Evm.GasPolicy.EthereumGasPolicy";
    private const string Contract = "Nethermind.Evm.GasPolicy.IGasPolicy`1";
    private static RefundExpression Variable(string name, ValueType type) => new(ExpressionKind.Variable, type, name, []);
    private static RefundExpression Project(RefundExpression receiver, string field, ValueType type) => new(ExpressionKind.Projection, type, field, [receiver]);

    internal static RefundPlan Build(CSharpCompilation compilation)
    {
        List<ExpressionSite> expressions = [];
        MethodDeclarationSyntax Method(string owner, string name) =>
            (MethodDeclarationSyntax)compilation.GetTypeByMetadataName(owner)!.GetMembers(name).OfType<IMethodSymbol>().Single().DeclaringSyntaxReferences.Single().GetSyntax();
        void Add(string role, MethodDeclarationSyntax method, ExpressionSyntax syntax, params (string Name, string Lean, ValueType Type)[] locals)
        {
            SemanticModel model = compilation.GetSemanticModel(method.SyntaxTree);
            IOperation operation = model.GetOperation(syntax) ?? throw new AdmissionException($"Missing source expression operation: {role}.");
            Dictionary<string, RefundExpression> environment = new(StringComparer.Ordinal)
            {
                ["tx"] = Variable("input", ValueType.Input), ["spec"] = Variable("input", ValueType.Input),
                ["opts"] = Variable("input", ValueType.Input), ["substate"] = Variable("input", ValueType.Input),
                ["gasPrice"] = Project(Variable("input", ValueType.Input), "gasPrice", ValueType.UInt256),
                ["gas"] = Variable("gas", ValueType.Gas), ["gasAfterExecution"] = Variable("gas", ValueType.Gas),
                ["intrinsicGasStandard"] = Project(Variable("input", ValueType.Input), "intrinsicStandard", ValueType.Gas),
                ["topLevelCreateStateGasCharged"] = Project(Variable("input", ValueType.Input), "topLevelCreateStateGasCharged", ValueType.Bool),
                ["codeInsertRefunds"] = Variable("count", ValueType.UInt64),
                ["txGasLimit"] = Variable("limit", ValueType.UInt64),
                ["remainingGas"] = Project(Variable("gas", ValueType.Gas), "value", ValueType.UInt64),
                ["stateReservoir"] = Project(Variable("gas", ValueType.Gas), "stateReservoir", ValueType.Int64),
                ["postIntrinsicStateReservoir"] = Project(Variable("input", ValueType.Input), "postIntrinsicStateReservoir", ValueType.Int64),
                ["intrinsicStateGas"] = Project(Project(Variable("input", ValueType.Input), "intrinsicStandard", ValueType.Gas), "stateReservoir", ValueType.Int64),
                ["floorGas"] = Project(Project(Variable("input", ValueType.Input), "floorGas", ValueType.Gas), "value", ValueType.UInt64),
            };
            foreach ((string name, string lean, ValueType type) in locals)
            {
                environment[name] = Variable(lean, type);
            }
            RefundExpression expression = Lower(operation, environment);
            expressions.Add(new(role, OperationLowering.Symbol(model.GetDeclaredSymbol(method)), Canonical(syntax), OperationLowering.Lower(operation), expression));
        }
        static ExpressionSyntax Local(MethodDeclarationSyntax method, string name) => method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == name).Initializer!.Value;
        static ExpressionSyntax Return(MethodDeclarationSyntax method) => method.ExpressionBody?.Expression ?? method.Body!.Statements.OfType<ReturnStatementSyntax>().Last().Expression!;

        MethodDeclarationSyntax pre = Method(Contract, "GetPreRefundGas");
        Add("preRefund.difference", pre, Local(pre, "preRefundGas"));
        Add("preRefund.range", pre, Local(pre, "inRange"), ("preRefundGas", "difference", ValueType.Int128));
        Add("preRefund.result", pre, Return(pre), ("inRange", "inRange", ValueType.Bool), ("preRefundGas", "difference", ValueType.Int128));

        MethodDeclarationSyntax initial = Method(Processor, "CalculateInitialStateReservoir");
        Add("initial.reservoir", initial, Return(initial), ("txGasLimit", "signedLimit", ValueType.Int64));
        MethodDeclarationSyntax halt = Method(Processor, "CompleteEip8037Halt");
        Add("halt.refundedIntrinsic", halt, Local(halt, "refundedIntrinsicStateGas"), ("initialStateReservoir", "initial", ValueType.Int64));
        Add("halt.stateFloor", halt, Local(halt, "postHaltIntrinsicStateGas"), ("refundedIntrinsicStateGas", "refundedIntrinsic", ValueType.Int64));

        MethodDeclarationSyntax code = Method(Policy, "GetCodeInsertExecutionRefund");
        IfStatementSyntax[] codeGuards = code.Body!.Statements.OfType<IfStatementSyntax>().ToArray();
        Add("code.zeroCount", code, codeGuards[0].Condition);
        Add("code.enabled", code, codeGuards[1].Condition);
        Add("code.amount", code, Return(code));

        MethodDeclarationSyntax validate = Method(Processor, "ShouldValidateGas");
        Add("payment.validate", validate, Return(validate));
        MethodDeclarationSyntax should = Method(Processor, "ShouldRefundGas");
        Add("payment.should", should, Return(should));

        MethodDeclarationSyntax refund = Method(Processor, "Refund");
        IfStatementSyntax[] refundGuards = refund.Body!.Statements.OfType<IfStatementSyntax>().ToArray();
        Add("refund.createGuard", refund, refundGuards[0].Condition);
        Add("refund.createAmount", refund, Local(refund, "topLevelCreateStateGas"));
        Add("refund.haltGuard", refund, refundGuards[1].Condition);
        Add("refund.preRefund", refund, Local(refund, "preRefundGas"));
        Add("refund.stateUsed", refund, Local(refund, "stateGasUsed"));
        Add("refund.quotient", refund, Local(refund, "refundQuotient"));
        InvocationExpressionSyntax settlementCall = (InvocationExpressionSyntax)Local(refund, "settlement");
        IInvocationOperation settlementOperation = (IInvocationOperation)compilation.GetSemanticModel(refund.SyntaxTree).GetOperation(settlementCall)!;
        if (settlementOperation.TargetMethod.ContainingType.ToDisplayString() != "Nethermind.Evm.TransactionProcessing.TransactionSettlementKernel" ||
            settlementOperation.TargetMethod.Name != "Calculate" || settlementOperation.Arguments.Length != 13)
        {
            throw new AdmissionException("The normal refund settlement edge changed.");
        }
        for (int ordinal = 0; ordinal < settlementCall.ArgumentList.Arguments.Count; ordinal++)
        {
            Add("settlement." + ordinal.ToString(CultureInfo.InvariantCulture), refund, settlementCall.ArgumentList.Arguments[ordinal].Expression,
                ("preRefundGas", "pre", ValueType.UInt64), ("codeInsertExecutionRefund", "codeRefund", ValueType.UInt64),
                ("floorGasLong", "floor", ValueType.UInt64), ("stateGasUsed", "used", ValueType.Int64), ("refundQuotient", "quotient", ValueType.UInt64));
        }
        ObjectCreationExpressionSyntax consumed = (ObjectCreationExpressionSyntax)Return(refund);
        for (int ordinal = 0; ordinal < consumed.ArgumentList!.Arguments.Count; ordinal++)
        {
            Add("consumed." + ordinal.ToString(CultureInfo.InvariantCulture), refund, consumed.ArgumentList.Arguments[ordinal].Expression,
                ("settlement", "settlement", ValueType.Consumed));
        }

        MethodDeclarationSyntax top = Method(Processor, "RefundOnTopLevelHalt");
        Add("halt.preRefund", top, Local(top, "preRefundGas"));
        Add("halt.claimed", top, Local(top, "executionRefund"), ("preRefundGas", "before", ValueType.UInt64), ("codeInsertExecutionRefund", "codeRefund", ValueType.UInt64));
        Add("halt.spent", top, Local(top, "spentGas"), ("preRefundGas", "before", ValueType.UInt64), ("executionRefund", "claimed", ValueType.UInt64));
        MethodDeclarationSyntax failed = Method(Processor, "RefundFailedEip8037Gas");
        Add("halt.paymentGuard", failed, failed.Body!.Statements.OfType<IfStatementSyntax>().Single().Condition, ("spentGas", "spent", ValueType.UInt64));
        ObjectCreationExpressionSyntax failedConsumed = (ObjectCreationExpressionSyntax)Return(failed);
        for (int ordinal = 0; ordinal < failedConsumed.ArgumentList!.Arguments.Count; ordinal++)
        {
            Add("halt.consumed." + ordinal.ToString(CultureInfo.InvariantCulture), failed, failedConsumed.ArgumentList.Arguments[ordinal].Expression,
                ("spentGas", "spent", ValueType.UInt64), ("blockGas", "blockGas", ValueType.UInt64),
                ("blockStateGas", "stateUsed", ValueType.Int64), ("gasRefund", "claimed", ValueType.UInt64));
        }

        MethodDeclarationSyntax reset = Method(Policy, "ResetForHalt");
        foreach (AssignmentExpressionSyntax assignment in reset.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            string field = ((MemberAccessExpressionSyntax)assignment.Left).Name.Identifier.ValueText;
            Add("reset." + field, reset, assignment.Right,
                ("initialStateReservoir", "reservoir", ValueType.Int64), ("initialStateGasUsed", "used", ValueType.Int64));
        }
        MethodDeclarationSyntax clear = Method(Policy, "ClearExecutionGas");
        Add("clear.Value", clear, ((AssignmentExpressionSyntax)clear.ExpressionBody!.Expression).Right);

        HaltStage[] stages = halt.Body!.Statements.OfType<ExpressionStatementSyntax>()
            .Select(statement => statement.Expression as InvocationExpressionSyntax ?? throw new AdmissionException("Non-call halt state mutation."))
            .Select(call => (compilation.GetSemanticModel(halt.SyntaxTree).GetOperation(call) as IInvocationOperation)?.TargetMethod.Name switch
            {
                "RefundRevertedExecutionStateGas" => HaltStage.RefundState,
                "ResetForHalt" => HaltStage.ResetState,
                "ClearExecutionGas" => HaltStage.ClearExecution,
                _ => throw new AdmissionException("Unknown halt mutation stage."),
            }).ToArray();
        if (!stages.SequenceEqual(new[] { HaltStage.RefundState, HaltStage.ResetState, HaltStage.ClearExecution }))
        {
            throw new AdmissionException("The state-baseline restoration must precede execution-gas clearing.");
        }
        return new(expressions.ToArray(), stages);
    }

    private static RefundExpression Lower(IOperation operation, IReadOnlyDictionary<string, RefundExpression> environment)
    {
        RefundExpression Child(IOperation child) => Lower(child, environment);
        RefundExpression Node(ExpressionKind kind, params RefundExpression[] arguments) => new(kind, Type(operation.Type), "", arguments);
        RefundExpression Input(string field, ValueType type) => Project(Variable("input", ValueType.Input), field, type);

        if (operation is IConversionOperation conversion)
        {
            RefundExpression operand = Child(conversion.Operand);
            if (conversion.OperatorMethod is not null && conversion.OperatorMethod.ContainingType.ToDisplayString() is not ("Nethermind.Int256.UInt256" or "System.Int128"))
            {
                throw new AdmissionException("Unadmitted user conversion in refund expression.");
            }
            return operand.Type == Type(conversion.Type) ? operand : Node(ExpressionKind.Convert, operand);
        }
        if (operation is IParameterReferenceOperation parameter && environment.TryGetValue(parameter.Parameter.Name, out RefundExpression? parameterValue)) return parameterValue;
        if (operation is ILocalReferenceOperation local && environment.TryGetValue(local.Local.Name, out RefundExpression? localValue)) return localValue;
        if (operation is ILiteralOperation literal)
        {
            string value = literal.ConstantValue.Value is bool boolean ? (boolean ? "true" : "false") :
                Convert.ToString(literal.ConstantValue.Value, CultureInfo.InvariantCulture) ?? throw new AdmissionException("Null literal is not scalar refund arithmetic.");
            return new(ExpressionKind.Literal, Type(literal.Type), value, []);
        }
        if (operation is IFieldReferenceOperation field)
        {
            if (field.Field.HasConstantValue)
            {
                return new(ExpressionKind.Literal, Type(field.Type), Convert.ToString(field.Field.ConstantValue, CultureInfo.InvariantCulture)!, []);
            }
            if (field.Field.ContainingType.ToDisplayString() == "Nethermind.Core.Eip7825Constants" && field.Field.Name == "DefaultTxGasLimitCap")
            {
                return Project(Variable("constants", ValueType.Constants), "executionCap", ValueType.UInt64);
            }
            if (field.Field.ContainingType.ToDisplayString() == "Nethermind.Core.Specs.SpecGasCosts" && field.Field.Name == "DestroyRefund" &&
                string.Concat(field.Syntax.DescendantTokens().Select(static token => token.Text)) == "spec.GasCosts.DestroyRefund")
            {
                return Input("destroyRefund", ValueType.UInt64);
            }
            if (field.Field.ContainingType.ToDisplayString() == "Nethermind.Evm.TransactionProcessing.TransactionSettlementResult")
            {
                string name = char.ToLowerInvariant(field.Field.Name[0]) + field.Field.Name[1..];
                if (new[] { "spentGas", "operationGas", "blockGas", "blockStateGas", "maxUsedGas", "gasRefund" }.Contains(name, StringComparer.Ordinal))
                {
                    return Project(Child(field.Instance!), name, Type(field.Type));
                }
            }
        }
        if (operation is IPropertyReferenceOperation property)
        {
            string owner = property.Property.ContainingType.ToDisplayString();
            if (owner == "Nethermind.Int256.UInt256" && property.Property.Name == "IsZero")
            {
                return Node(ExpressionKind.Equal, Child(property.Instance!), new(ExpressionKind.Literal, ValueType.UInt256, "0", []));
            }
            string? mapped = (owner, property.Property.Name) switch
            {
                ("Nethermind.Core.Transaction", "GasLimit") => "transactionGasLimit",
                ("Nethermind.Core.Transaction", "IsContractCreation") => "isContractCreation",
                ("Nethermind.Core.Transaction", "MaxFeePerGas") => "maxFeePerGas",
                ("Nethermind.Core.Transaction", "MaxPriorityFeePerGas") => "maxPriorityFeePerGas",
                ("Nethermind.Evm.TransactionSubstate", "IsError") => "isError",
                ("Nethermind.Evm.TransactionSubstate", "ShouldRevert") => "shouldRevert",
                ("Nethermind.Evm.TransactionSubstate", "Refund") => "refundCounter",
                (_, "IsEip8037Enabled") when owner.StartsWith("Nethermind.Core.Specs.", StringComparison.Ordinal) => "isEip8037Enabled",
                (_, "IsEip3529Enabled") when owner.StartsWith("Nethermind.Core.Specs.", StringComparison.Ordinal) => "isEip3529Enabled",
                (_, "IsEip7778Enabled") when owner.StartsWith("Nethermind.Core.Specs.", StringComparison.Ordinal) => "isEip7778Enabled",
                _ => null,
            };
            if (mapped is not null && Child(property.Instance!).Value == "input") return Input(mapped, Type(property.Type));
        }
        if (operation is ICoalesceOperation && string.Concat(operation.Syntax.DescendantTokens().Select(static token => token.Text)) == "substate.DestroyList?.Count??0")
        {
            return Input("destroyCount", ValueType.Int32);
        }
        if (operation is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } unary) return Node(ExpressionKind.Not, Child(unary.Operand));
        if (operation is IBinaryOperation binary)
        {
            ExpressionKind kind = binary.OperatorKind switch
            {
                BinaryOperatorKind.ConditionalAnd => ExpressionKind.And, BinaryOperatorKind.ConditionalOr => ExpressionKind.Or,
                BinaryOperatorKind.Equals => ExpressionKind.Equal, BinaryOperatorKind.NotEquals => ExpressionKind.NotEqual,
                BinaryOperatorKind.LessThan => ExpressionKind.Less, BinaryOperatorKind.LessThanOrEqual => ExpressionKind.LessEqual,
                BinaryOperatorKind.GreaterThan => ExpressionKind.Greater, BinaryOperatorKind.GreaterThanOrEqual => ExpressionKind.GreaterEqual,
                BinaryOperatorKind.Add => ExpressionKind.Add, BinaryOperatorKind.Subtract => ExpressionKind.Subtract,
                BinaryOperatorKind.Multiply => ExpressionKind.Multiply, BinaryOperatorKind.Divide => ExpressionKind.Divide,
                _ => throw new AdmissionException($"Unadmitted refund binary operation: {binary.OperatorKind}."),
            };
            return Node(kind, Child(binary.LeftOperand), Child(binary.RightOperand));
        }
        if (operation is IConditionalOperation conditional) return Node(ExpressionKind.Conditional, Child(conditional.Condition), Child(conditional.WhenTrue), Child(conditional.WhenFalse!));
        if (operation is IInvocationOperation call)
        {
            string owner = call.TargetMethod.ContainingType.ToDisplayString();
            IArgumentOperation[] args = call.Arguments.OrderBy(static argument => argument.Parameter!.Ordinal).ToArray();
            if (owner == "System.Math" && call.TargetMethod.Name is "Min" or "Max")
            {
                return Node(call.TargetMethod.Name == "Min" ? ExpressionKind.Min : ExpressionKind.Max, args.Select(argument => Child(argument.Value)).ToArray());
            }
            if (owner.StartsWith("Nethermind.Evm.GasPolicy.IGasPolicy<", StringComparison.Ordinal))
            {
                if (call.TargetMethod.Name == "GetCreateStateCost") return Project(Variable("constants", ValueType.Constants), "createStateCost", ValueType.Int64);
                if (call.TargetMethod.Name == "GetPreRefundGas") return Node(ExpressionKind.PreRefundGas, Child(args[0].Value), Child(args[1].Value));
                string? gasField = call.TargetMethod.Name switch { "GetRemainingGas" => "value", "GetStateReservoir" => "stateReservoir", "GetStateGasUsed" => "stateGasUsed", _ => null };
                if (gasField is not null) return Project(Child(args[0].Value), gasField, Type(call.Type));
            }
            if (owner.StartsWith("Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<", StringComparison.Ordinal))
            {
                if (call.TargetMethod.Name == "ShouldRefundGas") return Node(ExpressionKind.ShouldRefundGas, Variable("input", ValueType.Input));
                if (call.TargetMethod.Name == "ShouldValidateGas") return Node(ExpressionKind.ShouldValidateGas, Variable("input", ValueType.Input));
                if (call.TargetMethod.Name == "CalculateClaimableRefund") return Node(ExpressionKind.ClaimableRefund, Child(args[0].Value), Child(args[1].Value));
            }
            if (owner == "System.Enum" && call.TargetMethod.Name == "HasFlag" &&
                string.Concat(call.Syntax.DescendantTokens().Select(static token => token.Text)) == "opts.HasFlag(ExecutionOptions.SkipValidation)")
            {
                return Input("skipValidation", ValueType.Bool);
            }
        }
        throw new AdmissionException($"Refund expression is not in the typed lowering grammar: {operation.Kind}: {operation.Syntax}.");
    }

    private static ValueType Type(ITypeSymbol? type) => type?.SpecialType switch
    {
        SpecialType.System_Boolean => ValueType.Bool, SpecialType.System_UInt64 => ValueType.UInt64,
        SpecialType.System_Int64 => ValueType.Int64, SpecialType.System_Int32 => ValueType.Int32,
        _ when type?.ToDisplayString() == "System.Int128" => ValueType.Int128,
        _ when type?.ToDisplayString() == "Nethermind.Int256.UInt256" => ValueType.UInt256,
        _ => throw new AdmissionException($"Unadmitted scalar expression type: {type}."),
    };

    private static string Canonical(SyntaxNode node) => string.Join(" ", node.DescendantTokens().Select(static token => token.Text));
}
