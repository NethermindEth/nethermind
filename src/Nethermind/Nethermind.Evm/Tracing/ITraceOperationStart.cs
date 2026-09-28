// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing;

/// <summary>Receives opcode preconditions and calculated costs before the instruction changes execution state.</summary>
public interface ITraceOperationStart : ITxTracer
{
    /// <summary>Reports a known constant gas cost and the opcode's stack requirements.</summary>
    void ReportOperationStart(ulong gasCost, int stackHead, int stackInputs, int stackGrowth);

    /// <summary>Reports a completed dynamic gas calculation before execution, including any preexecution error.</summary>
    void ReportOperationReady(ulong gasCost, string? error);

}
