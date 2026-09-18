// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nethermind.Evm.Lean.OrdinaryEvmCompletionExtractor;

internal static class LeanEmitter
{
    internal static byte[] Emit(string root, ArtifactDocument document, string irHash)
    {
        string template = File.ReadAllText(CompilerReferences.Within(root, SourceAdmission.PackagePath + "/CompletionKernel.lean.template"));
        Dictionary<string, string> replacements = new(StringComparer.Ordinal)
        {
            ["SOURCE_CLOSURE"] = document.SourceClosure,
            ["SOURCE_IR"] = irHash,
            ["FAILURE_PREDICATE"] = Scalar("vm.failed", new()
            {
                ["substate.ShouldRevert"] = "shouldRevert terminal", ["substate.IsError"] = "isError terminal",
            }),
            ["REFUND_FLOOR"] = RefundArgument("floorGas", "gas.FloorGas", "i.tail.intrinsicFloorGas"),
            ["REFUND_INCOMING"] = RefundArgument("unspentGas", "gasAvailable", "i.tail.vm.postGas"),
            ["REFUND_STANDARD"] = RefundArgument("intrinsicGasStandard", "gas.Standard", "preparationGas (prepared i).executionIntrinsicGasStandard"),
            ["REFUND_RESERVOIR"] = RefundArgument("postIntrinsicStateReservoir", "postIntrinsicStateReservoir", "(prepared i).postIntrinsicStateReservoir"),
            ["REFUND_AUTH_COUNT"] = RefundArgument("codeInsertRefunds", "(ulong)delegationRefunds", "(prepared i).delegationRefunds"),
            ["REFUND_PRICE"] = RefundArgument("gasPrice", "VirtualMachine.TxExecutionContext.GasPrice", "i.preparation.handoff.context.opcodeGasPrice"),
            ["FRAME_GAS_COPY"] = AssignmentRight("vm.gas-copy", "state.Gas", "i.tail.vm.postGas"),
            ["EXECUTION_TOTAL"] = Compound("processor.execution", "_blockCumulativeExecutionGas", "spentGas.EffectiveBlockGas", "before.executionGas", "R.effectiveBlockGas gas"),
            ["STATE_TOTAL"] = Compound("processor.state", "_blockCumulativeStateGas", "spentGas.BlockStateGas", "before.stateGas", "gas.blockStateGas"),
            ["BASE_PRICE"] = Scalar("fees.base", new()
            {
                ["header.BaseFeePerGas"] = "i.tail.block.baseFeePerGas", ["effectiveGasPrice"] = "i.preparation.handoff.context.opcodeGasPrice",
            }),
            ["PREMIUM"] = "(" + Scalar("fees.premium", new()
            {
                ["premiumPerGas"] = "i.tail.premiumPerGas", ["spentGas"] = "spent",
            }) + ") % F.uint256Modulus",
        };
        foreach ((string name, string expression) in replacements)
        {
            string marker = "@@" + name + "@@";
            if (!template.Contains(marker, StringComparison.Ordinal)) throw new AdmissionException("template.missing." + name);
            template = template.Replace(marker, expression, StringComparison.Ordinal);
        }
        if (template.Contains("@@", StringComparison.Ordinal)) throw new AdmissionException("template.unresolved");
        return Encoding.UTF8.GetBytes(template.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n");

        ExpressionSite Site(string role) => document.Source.Plan.Expressions.Single(site => site.Role == role);

        string RefundArgument(string name, string expected, string lean)
        {
            ExpressionSite site = Site("refund.input");
            OperationTerm[] arguments = site.Operation.Children.Where(static child => child.Kind == "Argument").ToArray();
            InvocationExpressionSyntax syntax = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression(site.Syntax);
            int index = Array.FindIndex(arguments, argument => argument.Symbol.Contains(":" + name + ":", StringComparison.Ordinal));
            if (index < 0 || index >= syntax.ArgumentList.Arguments.Count || syntax.ArgumentList.Arguments[index].Expression.ToString() != expected)
                throw new AdmissionException("lower.refund." + name);
            return lean;
        }

        string AssignmentRight(string role, string expected, string lean)
        {
            if (SyntaxFactory.ParseExpression(Site(role).Syntax) is not AssignmentExpressionSyntax assignment || assignment.Right.ToString() != expected)
                throw new AdmissionException("lower.assignment." + role);
            return lean;
        }

        string Compound(string role, string left, string right, string leanLeft, string leanRight)
        {
            if (SyntaxFactory.ParseExpression(Site(role).Syntax) is not AssignmentExpressionSyntax assignment ||
                !assignment.IsKind(SyntaxKind.AddAssignmentExpression) || assignment.Left.ToString() != left || assignment.Right.ToString() != right)
                throw new AdmissionException("lower.counter." + role);
            return leanLeft + " + " + leanRight;
        }

        string Scalar(string role, Dictionary<string, string> leaves) => Lower(SyntaxFactory.ParseExpression(Site(role).Syntax), leaves);
    }

    private static string Lower(ExpressionSyntax expression, IReadOnlyDictionary<string, string> leaves)
    {
        if (leaves.TryGetValue(expression.ToString(), out string? leaf)) return leaf;
        return expression switch
        {
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression) =>
                $"({Lower(binary.Left, leaves)}) || ({Lower(binary.Right, leaves)})",
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.MultiplyExpression) =>
                $"({Lower(binary.Left, leaves)}) * ({Lower(binary.Right, leaves)})",
            InvocationExpressionSyntax call when call.Expression.ToString() == "UInt256.Min" && call.ArgumentList.Arguments.Count == 2 =>
                $"min ({Lower(call.ArgumentList.Arguments[0].Expression, leaves)}) ({Lower(call.ArgumentList.Arguments[1].Expression, leaves)})",
            _ => throw new AdmissionException("lower.unsupported." + expression.Kind()),
        };
    }
}
