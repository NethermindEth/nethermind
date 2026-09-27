// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

/// <summary>Receives calls rejected before an execution frame is created.</summary>
public interface ITraceRejectedCall : ITxTracer
{
    /// <summary>Records a rejected call whose forwarded gas is returned to its caller.</summary>
    void ReportRejectedCall(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, EvmExceptionType error);
}
