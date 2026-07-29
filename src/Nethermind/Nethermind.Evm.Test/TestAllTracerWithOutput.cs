// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Test
{
    public class TestAllTracerWithOutput : TxTracer
    {
        public TestAllTracerWithOutput() => IsTracingAccess = true;

        public override bool IsTracingReceipt => true;
        public override bool IsTracingActions => true;
        public override bool IsTracingOpLevelStorage => true;
        public override bool IsTracingMemory => true;
        public override bool IsTracingInstructions => true;
        public override bool IsTracingRefunds => true;
        public override bool IsTracingCode => true;
        public override bool IsTracingStack => true;
        public override bool IsTracingState => true;
        public override bool IsTracingStorage => true;
        public override bool IsTracingBlockHash => true;
        public new bool IsTracingAccess { get { return base.IsTracingAccess; } set { base.IsTracingAccess = value; } }
        public override bool IsTracingFees => true;

        public byte[]? ReturnValue { get; private set; }

        public ulong GasSpent { get; private set; }

        public string? Error { get; private set; }

        public byte StatusCode { get; private set; }

        public GasConsumed GasConsumedResult { get; private set; }
        public ulong CumulativeRegularGasUsed { get; private set; }

        public long Refund { get; private set; }

        public readonly record struct ActionTrace(ulong Gas, UInt256 Value, Address From, Address To, ExecutionType CallType, bool IsPrecompileCall);

        public List<ActionTrace> Actions { get; } = [];

        public List<EvmExceptionType> ReportedActionErrors { get; set; } = [];

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            CumulativeRegularGasUsed += gasSpent.EffectiveBlockGas;
            GasSpent = gasSpent.SpentGas;
            GasConsumedResult = gasSpent;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            CumulativeRegularGasUsed += gasSpent.EffectiveBlockGas;
            GasSpent = gasSpent.SpentGas;
            GasConsumedResult = gasSpent;
            Error = error;
            ReturnValue = output ?? [];
            StatusCode = Evm.StatusCode.Failure;
        }

        public override void ReportActionError(EvmExceptionType exceptionType) => ReportedActionErrors.Add(exceptionType);

        public override void ReportRefund(long refund) => Refund += refund;

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
            => Actions.Add(new ActionTrace(gas, value, from, to, callType, isPrecompileCall));
    }
}
