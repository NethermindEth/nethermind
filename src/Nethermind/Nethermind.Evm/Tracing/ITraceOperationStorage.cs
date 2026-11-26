// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing;

/// <summary>Receives storage effects calculated for an opcode even when execution fails.</summary>
public interface ITraceOperationStorage : ITxTracer
{
    /// <summary>Records a refund calculated by a failed storage instruction without changing execution state.</summary>
    void ReportStorageRefund(long refund);
}
