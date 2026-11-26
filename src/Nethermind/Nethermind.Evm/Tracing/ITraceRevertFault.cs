// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Tracing;

/// <summary>Opts into an instruction fault notification after a successfully executed REVERT.</summary>
public interface ITraceRevertFault : ITxTracer;
