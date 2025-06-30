// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Evm.Tracing
{
    public class CallOutputTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public byte[]? ReturnValue { get; set; }

        public long GasSpent { get; set; }
        public long OperationGas { get; set; }

        public string? Error { get; set; }

        public byte StatusCode { get; set; }

        public EvmExceptionType EvmExceptionType { get; set; }

        public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            GasSpent = gasSpent.SpentGas;
            OperationGas = gasSpent.OperationGas;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, EvmExceptionType evmException, Hash256? stateRoot = null)
        {
            GasSpent = gasSpent.SpentGas;
            OperationGas = gasSpent.OperationGas;
            Error = error;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Failure;
            EvmExceptionType = evmException;
        }
    }
}
