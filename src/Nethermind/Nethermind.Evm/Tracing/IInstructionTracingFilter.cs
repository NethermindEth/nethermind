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
    /// Bit N requests snapshots for opcode N. <see cref="UInt256.MaxValue"/> requests full instruction capture.
    /// Dispatch samples the mask once per transaction; implementations must keep it stable for that transaction.
    /// This is not a filter for every callback: errors, execution-segment gas checkpoints and implicit STOP
    /// can still be reported. Composite tracers can also deliver opcodes requested by sibling tracers.
    /// </summary>
    UInt256 InstructionMask { get; }
}
