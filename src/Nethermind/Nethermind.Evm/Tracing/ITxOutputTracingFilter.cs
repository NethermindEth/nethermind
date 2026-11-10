// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing;

/// <summary>
/// Optionally tells the processor whether a tracer reads the transaction output.
/// </summary>
public interface ITxOutputTracingFilter
{
    /// <summary>
    /// Whether the output passed to <c>MarkAsSuccess</c> and <c>MarkAsFailed</c> is read. When <see langword="false"/>,
    /// the processor passes an empty output and may stage the top-level output in reused scratch.
    /// The processor samples it once per transaction; implementations must keep it stable for that transaction.
    /// </summary>
    bool IsTracingOutput { get; }
}
