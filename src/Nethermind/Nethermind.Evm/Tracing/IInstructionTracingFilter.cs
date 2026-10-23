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
    /// Completion callbacks are emitted only for an instruction whose start was reported. Errors and VM-trace gas
    /// updates can still be reported independently. Opcode mutation callbacks are not filtered. Opted-in implicit STOP
    /// callbacks and opcodes requested by sibling composite tracers can also be delivered.
    /// </summary>
    UInt256 InstructionMask { get; }

    /// <summary>
    /// Whether opcodes outside <see cref="InstructionMask"/> still report their stack, memory and gas mutations.
    /// </summary>
    /// <remarks>
    /// A tracer that reads state only at the instructions it requests returns <c>false</c>; dispatch then runs the other
    /// opcodes untraced. Applies only when the mask is partial.
    /// </remarks>
    bool ObservesUnmaskedMutations => true;
}
