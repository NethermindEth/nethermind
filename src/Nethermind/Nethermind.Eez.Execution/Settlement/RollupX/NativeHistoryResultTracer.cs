// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed class NativeHistoryResultTracer : TxTracer
{
    internal bool Valid { get; private set; }

    internal NativeHistoryResultTracer() => IsTracingReceipt = true;

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
        Valid = output.Length == 0 && logs.Length == 0;
}
