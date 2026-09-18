// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.ReceiptTerminalFoldExtractor;

/// <summary>
/// Admits only the statement control flow lowered by the receipt fold.
/// </summary>
/// <remarks>
/// Branch paths locate effects but cannot rule out an earlier exit. Each admitted
/// tree fixes every branch, statement and terminal return; no jump, loop, handler,
/// deferred body or explicit exceptional exit can bypass a modeled effect.
/// Expression values and branch polarity are checked by the typed lowering.
/// </remarks>
internal static class ReceiptTerminalFoldControlFlow
{
    internal static void Validate(string member, SemanticStep[] steps)
    {
        // E: expression, L: local, R: return, A: expression body,
        // I: if, B: block, F: else. Parentheses retain the complete nesting.
        string expected = member switch
        {
            "MarkAsSuccess" or "MarkAsFailed" => "E I(B(E)) I(B(E))",
            "BuildFailedReceipt" => "L E R",
            "BuildReceipt" => "L L L L L I(B(E E)) I(B(E)) R",
            "UpdateCumulativeGasTracking" => "E L E I(B(E)) E E R",
            "TakeSnapshot" or "CombineBlockGas" or "Combine" or "CalculateEffectiveGasPrice" or "FromTotals" => "A",
            "Restore" => "L I(B(E E)) E E L L E E",
            "Accumulate" => "L L L R",
            "FinalizeTransaction" =>
                "I(B(E)) I(E) " +
                "I(B(E I(B(E) F(B(I(B(E)) E E)))) F(I(B(E) F(B(E I(B(E))))))) " +
                "I(B(L I(B(E E)) I(B(L L I(B(E)) E) F(B(L E))))) R",
            _ => throw Rejected(member, "member has no admitted statement grammar"),
        };
        string actual = Shape(steps);
        if (actual != expected)
            throw Rejected(member, $"statement control flow differs: expected '{expected}', found '{actual}'");

        string Shape(SemanticStep[] children)
        {
            for (int index = 1; index < children.Length; index++)
            {
                if (children[index - 1].End > children[index].Start)
                    throw Rejected(member, "statement ranges overlap or disagree with execution order");
            }
            return string.Join(" ", children.Select(StepShape));
        }

        string StepShape(SemanticStep step)
        {
            CheckExpression(step.Expression);
            CheckExpression(step.DeclaredTarget);
            string kind = step.Kind switch
            {
                "ExpressionStatement" => "E",
                "LocalDeclarationStatement" => "L",
                "ReturnStatement" => "R",
                "ArrowExpressionClause" => "A",
                "IfStatement" => "I",
                "Block" => "B",
                "ElseClause" => "F",
                _ => throw Rejected(member, $"unsupported statement {step.Kind}"),
            };
            if (kind is "I" or "B" or "F") return kind + "(" + Shape(step.Children) + ")";
            if (step.Children.Length != 0)
                throw Rejected(member, $"unexpected child statements in {step.Kind}");
            return kind;
        }

        void CheckExpression(SemanticExpression? expression)
        {
            if (expression is null) return;
            if (expression.Kind is "ThrowExpression" or "SimpleLambdaExpression" or "ParenthesizedLambdaExpression" or
                "AnonymousMethodExpression" or "AwaitExpression" or "SwitchExpression" or "QueryExpression")
                throw Rejected(member, $"unsupported control-flow expression {expression.Kind}");
            foreach (SemanticExpression child in expression.Children) CheckExpression(child);
        }
    }

    private static ExtractionException Rejected(string member, string reason) =>
        new($"RECEIPT_CONTROL_FLOW: {member}: {reason}.");
}
