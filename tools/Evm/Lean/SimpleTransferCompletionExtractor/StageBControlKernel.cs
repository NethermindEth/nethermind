// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;

internal readonly record struct StageBTickResult(bool Exhausted, long RemainingFuel);
internal enum StageBEdgeKind { Jump, Return, InvalidCondition, MissingEdge, InvalidEdge, MissingSuspension }
internal readonly record struct StageBEdgeResult(StageBEdgeKind Kind, int Destination);
internal readonly record struct StageBBlockCursor<T>(int Ordinal, T Carried);
internal readonly record struct StageBFinishResult<T>(StageBEdgeKind Kind, StageBBlockCursor<T> Cursor);
internal enum StageBLocalReturnKind { ReturnedValue, ReceiverValue, ReceiverLocation }

internal static class StageBControlKernel
{
    internal static StageBTickResult Tick(long remainingFuel)
    {
        if (remainingFuel == 0) return new(true, 0);
        return new(false, remainingFuel - 1);
    }

    internal static StageBEdgeResult SelectEdge(string conditionKind, bool valueIsBoolean, bool booleanValue,
        bool hasFallThrough, int fallThroughDestination, bool fallThroughReturns,
        bool hasConditional, int conditionalDestination, bool conditionalReturns)
    {
        if (conditionKind != "None" && !valueIsBoolean) return new(StageBEdgeKind.InvalidCondition, -1);
        bool takeConditional = hasConditional &&
            (conditionKind == "WhenTrue" && booleanValue || conditionKind == "WhenFalse" && !booleanValue);
        bool hasEdge = takeConditional ? hasConditional : hasFallThrough;
        if (!hasEdge) return new(StageBEdgeKind.MissingEdge, -1);
        int destination = takeConditional ? conditionalDestination : fallThroughDestination;
        bool returns = takeConditional ? conditionalReturns : fallThroughReturns;
        if (destination < 0) return new(returns ? StageBEdgeKind.Return : StageBEdgeKind.InvalidEdge, -1);
        return new(StageBEdgeKind.Jump, destination);
    }

    internal static StageBFinishResult<T> FinishBlock<T>(StageBPrefixExit exit, StageBBlockCursor<T> cursor,
        string conditionKind, bool valueIsBoolean, bool booleanValue,
        bool hasFallThrough, int fallThroughDestination, bool fallThroughReturns,
        bool hasConditional, int conditionalDestination, bool conditionalReturns)
    {
        if (exit == StageBPrefixExit.Return) return new(StageBEdgeKind.Return, cursor);
        if (exit == StageBPrefixExit.Suspend) return new(StageBEdgeKind.MissingSuspension, cursor);
        StageBEdgeResult edge = SelectEdge(conditionKind, valueIsBoolean, booleanValue,
            hasFallThrough, fallThroughDestination, fallThroughReturns,
            hasConditional, conditionalDestination, conditionalReturns);
        return new(edge.Kind, edge.Kind == StageBEdgeKind.Jump ? new(edge.Destination, cursor.Carried) : cursor);
    }

    internal static StageBLocalReturnKind SelectLocalReturn(bool constructing, bool valueMode)
    {
        if (!constructing) return StageBLocalReturnKind.ReturnedValue;
        return valueMode ? StageBLocalReturnKind.ReceiverValue : StageBLocalReturnKind.ReceiverLocation;
    }

    internal static bool ShouldReadTransparent(bool preservesOperand, bool valueMode) => !preservesOperand && valueMode;
}
