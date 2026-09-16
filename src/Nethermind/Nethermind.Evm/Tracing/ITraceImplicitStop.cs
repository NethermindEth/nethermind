// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing;

/// <summary>
/// Marks an instruction tracer that records the implicit <see cref="Instruction.STOP"/> read immediately
/// after execution falls through the end of non-empty bytecode.
/// </summary>
public interface ITraceImplicitStop : ITxTracer;
