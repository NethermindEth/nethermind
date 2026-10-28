// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing;

/// <summary>Receives an opcode's calculated gas cost independently of its remaining gas.</summary>
public interface ITraceOperationGasCost : ITxTracer
{
    /// <summary>Sets the calculated cost of the current opcode, including costs it could not pay.</summary>
    void ReportOperationGasCost(ulong gasCost);
}
