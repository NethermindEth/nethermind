// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing
{
    public class CallOutputTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public byte[] ReturnValue { get; set; } = null!;

        public long GasSpent { get; set; }

        public string? Error { get; set; }

        public byte StatusCode { get; set; }

        public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            GasSpent = gasSpent;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        {
            GasSpent = gasSpent;
            Error = error;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Failure;
        }
    }
}
