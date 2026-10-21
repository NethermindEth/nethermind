// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.OpcodeTracing.Plugin;

/// <summary>
/// Specifies the tracing mode for opcode collection.
/// </summary>
public enum TracingMode
{
    /// <summary>
    /// Trace opcodes as blocks are processed in real-time during sync or as new blocks arrive.
    /// Adds minimal overhead (<5%) to block processing.
    /// </summary>
    RealTime,

    /// <summary>
    /// Trace opcodes by reading historical blocks from the database.
    /// Does not impact live sync performance but requires blocks to be already synced.
    /// </summary>
    Retrospective,

    /// <summary>
    /// Trace opcodes by replaying historical transactions with actual EVM execution.
    /// Provides accurate opcode counts including internal calls (CALL, DELEGATECALL, etc.).
    /// Requires historical state to be available (archive node or blocks within pruning window).
    /// Supports parallel block processing via MaxDegreeOfParallelism configuration.
    /// </summary>
    RetrospectiveExecution
}
