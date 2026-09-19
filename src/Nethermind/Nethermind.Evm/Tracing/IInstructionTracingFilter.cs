// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

/// <summary>
/// Optionally restricts instruction snapshots without changing EVM execution semantics.
/// </summary>
public interface IInstructionTracingFilter
{
    /// <summary>
    /// Bit N requests instruction starts and pre-execution snapshots for opcode N.
    /// <see cref="UInt256.MaxValue"/> requests full instruction capture.
    /// Dispatch samples the mask once per transaction; implementations must keep it stable for that transaction.
    /// Completion and error callbacks are emitted only for an instruction whose start was reported.
    /// Opcode mutation callbacks are not filtered. Opted-in implicit STOP callbacks and opcodes requested by sibling
    /// composite tracers can also be delivered.
    /// </summary>
    UInt256 InstructionMask { get; }
}
