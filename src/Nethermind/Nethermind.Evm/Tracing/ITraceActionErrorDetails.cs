// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing;

/// <summary>Receives additional diagnostic information before the current action fails.</summary>
public interface ITraceActionErrorDetails : ITxTracer
{
    /// <summary>Sets the diagnostic message for the current action's subsequent error.</summary>
    void ReportActionErrorDetails(string error);
}
