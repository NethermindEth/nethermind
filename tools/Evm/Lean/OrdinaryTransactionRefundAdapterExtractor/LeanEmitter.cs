// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Text;

namespace Nethermind.Evm.Lean.OrdinaryTransactionRefundAdapterExtractor;

internal static class LeanEmitter
{
    private static readonly HashSet<string> Variables = new(StringComparer.Ordinal)
    {
        "input", "gas", "constants", "count", "limit", "signedLimit", "difference", "inRange", "initial", "refundedIntrinsic",
        "before", "codeRefund", "claimed", "spent", "pre", "floor", "used", "quotient", "settlement", "blockGas", "stateUsed", "reservoir",
    };
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "transactionGasLimit", "gasPrice", "maxFeePerGas", "maxPriorityFeePerGas", "skipValidation", "isContractCreation",
        "isEip8037Enabled", "isEip3529Enabled", "isEip7778Enabled", "isError", "shouldRevert", "refundCounter", "destroyCount",
        "destroyRefund", "codeInsertRefundCount", "incomingGas", "intrinsicStandard", "floorGas", "postIntrinsicStateReservoir",
        "topLevelCreateStateGasCharged", "value", "stateReservoir", "stateGasUsed", "stateGasSpill", "stateGasSpillRefunded",
        "executionCap", "createStateCost", "newAccountCost", "perAuthorizationCost", "legacyRefundQuotient", "eip3529RefundQuotient",
        "spentGas", "operationGas", "blockGas", "blockStateGas", "maxUsedGas", "gasRefund",
    };

    internal static byte[] Emit(string root, SourceAdmission.AdmissionResult admission, string sourceDigest, string irDigest)
    {
        if (admission.Plan.Expressions.Length != 50 || admission.Plan.Expressions.Select(static site => site.Role).Distinct(StringComparer.Ordinal).Count() != 50 ||
            !admission.Plan.HaltStages.SequenceEqual(new[] { HaltStage.RefundState, HaltStage.ResetState, HaltStage.ClearExecution }))
        {
            throw new AdmissionException("The closed refund expression/stage roster changed.");
        }
        string text = File.ReadAllText(CompilerReferences.Within(root, SourceAdmission.PackagePath + "/RefundKernel.lean.template"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (ExpressionSite site in admission.Plan.Expressions)
        {
            string marker = "{{" + site.Role + "}}";
            if (!text.Contains(marker, StringComparison.Ordinal)) throw new AdmissionException("Unused source-derived refund expression: " + site.Role);
            text = text.Replace(marker, Expression(site.Expression), StringComparison.Ordinal);
        }
        RefundConstants constants = admission.Constants;
        text = text.Replace("{{SOURCE_DIGEST}}", sourceDigest, StringComparison.Ordinal)
            .Replace("{{IR_DIGEST}}", irDigest, StringComparison.Ordinal)
            .Replace("{{CONSTANTS}}", $"{{ executionCap := {constants.ExecutionCap}, createStateCost := {constants.CreateStateCost}, newAccountCost := {constants.NewAccountCost}, perAuthorizationCost := {constants.PerAuthorizationCost}, legacyRefundQuotient := {constants.LegacyRefundQuotient}, eip3529RefundQuotient := {constants.Eip3529RefundQuotient} }}", StringComparison.Ordinal);
        if (text.Contains("{{", StringComparison.Ordinal) || text.Contains("sorry", StringComparison.Ordinal) ||
            text.Contains("theorem ", StringComparison.Ordinal) || text.Contains("axiom ", StringComparison.Ordinal) || text.Contains("native_decide", StringComparison.Ordinal))
        {
            throw new AdmissionException("The refund generated module contains an unresolved marker or proof declaration.");
        }
        return Encoding.UTF8.GetBytes(text);
    }

    internal static string Expression(RefundExpression expression)
    {
        string A(int ordinal) => Expression(expression.Arguments[ordinal]);
        string Binary(string op) => $"({A(0)} {op} {A(1)})";
        string Arithmetic(string op)
        {
            string value = $"(({A(0)} : Int) {op} ({A(1)} : Int))";
            return expression.Type switch
            {
                ValueType.UInt64 => $"(T.wrapUInt64 {value})",
                ValueType.Int64 => $"(T.wrapInt64 {value})",
                ValueType.Int128 => value,
                ValueType.UInt256 => $"(({A(0)} {op} {A(1)}) % uint256Modulus)",
                _ => throw new AdmissionException("Unsupported emitted refund arithmetic type."),
            };
        }

        int arity = expression.Kind switch
        {
            ExpressionKind.Variable or ExpressionKind.Literal => 0,
            ExpressionKind.Projection or ExpressionKind.Convert or ExpressionKind.Not or ExpressionKind.ShouldRefundGas or ExpressionKind.ShouldValidateGas => 1,
            ExpressionKind.Conditional => 3,
            _ => 2,
        };
        if (!Enum.IsDefined(expression.Kind) || !Enum.IsDefined(expression.Type) || expression.Arguments is null || expression.Arguments.Length != arity || expression.Arguments.Any(static child => child is null))
        {
            throw new AdmissionException("Invalid refund expression shape.");
        }
        if (expression.Kind is not (ExpressionKind.Variable or ExpressionKind.Literal or ExpressionKind.Projection) && expression.Value.Length != 0)
        {
            throw new AdmissionException("Unexpected refund expression payload.");
        }

        return expression.Kind switch
        {
            ExpressionKind.Variable when Variables.Contains(expression.Value) => expression.Value,
            ExpressionKind.Literal when expression.Type == ValueType.Bool && expression.Value is "true" or "false" => expression.Value,
            ExpressionKind.Literal when expression.Type != ValueType.Bool && BigInteger.TryParse(expression.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger integer) && integer.ToString(CultureInfo.InvariantCulture) == expression.Value => "(" + expression.Value + ")",
            ExpressionKind.Projection when Fields.Contains(expression.Value) => $"({A(0)}).{expression.Value}",
            ExpressionKind.Convert => Convert(expression, A(0)),
            ExpressionKind.Not => $"(!{A(0)})",
            ExpressionKind.And => Binary("&&"), ExpressionKind.Or => Binary("||"),
            ExpressionKind.Equal => Binary("=="), ExpressionKind.NotEqual => Binary("!="),
            ExpressionKind.Less => $"(decide {Binary("<")})", ExpressionKind.LessEqual => $"(decide {Binary("≤")})",
            ExpressionKind.Greater => $"(decide {Binary(">")})", ExpressionKind.GreaterEqual => $"(decide {Binary("≥")})",
            ExpressionKind.Add => Arithmetic("+"), ExpressionKind.Subtract => Arithmetic("-"), ExpressionKind.Multiply => Arithmetic("*"),
            ExpressionKind.Divide => Binary("/"), ExpressionKind.Min => $"(min {A(0)} {A(1)})", ExpressionKind.Max => $"(max {A(0)} {A(1)})",
            ExpressionKind.Conditional => $"(if {A(0)} then {A(1)} else {A(2)})",
            ExpressionKind.PreRefundGas => $"(preRefundGas {A(0)} {A(1)})",
            ExpressionKind.ShouldRefundGas => $"(shouldRefundGas {A(0)})",
            ExpressionKind.ShouldValidateGas => $"(shouldValidateGas {A(0)})",
            ExpressionKind.ClaimableRefund => $"(min ({A(0)} / refundQuotient constants input) {A(1)})",
            ExpressionKind.SaturatingSubtract => $"(T.saturatingSubUInt64 {A(0)} {A(1)})",
            _ => throw new AdmissionException("Unsupported refund expression payload."),
        };
    }

    private static string Convert(RefundExpression expression, string operand)
    {
        ValueType from = expression.Arguments[0].Type;
        return expression.Type switch
        {
            ValueType.Int128 => $"({operand} : Int)",
            ValueType.Int64 when from == ValueType.UInt64 => $"(T.uint64ToInt64 {operand})",
            ValueType.Int64 when from == ValueType.Int32 => operand,
            ValueType.UInt64 when from is ValueType.Int64 or ValueType.Int32 or ValueType.Int128 => $"(T.wrapUInt64 {operand})",
            ValueType.UInt256 when from == ValueType.UInt64 => operand,
            _ when from == expression.Type => operand,
            _ => throw new AdmissionException($"Unsupported emitted refund conversion: {from} -> {expression.Type}."),
        };
    }
}
