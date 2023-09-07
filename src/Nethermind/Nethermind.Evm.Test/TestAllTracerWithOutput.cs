// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.Test
{
    public class TestAllTracerWithOutput : TxTracer
    {
        public TestAllTracerWithOutput()
        {
            IsTracingAccess = true;
        }

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

        public long GasSpent { get; private set; }

        public string? Error { get; private set; }

        public byte StatusCode { get; private set; }

        public long Refund { get; private set; }

        public List<EvmExceptionType> ReportedActionErrors { get; set; } = new List<EvmExceptionType>();

        public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            GasSpent = gasSpent;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Keccak? stateRoot = null)
        {
            GasSpent = gasSpent;
            Error = error;
            ReturnValue = output ?? Array.Empty<byte>();
            StatusCode = Evm.StatusCode.Failure;
        }

        public override void ReportActionError(EvmExceptionType exceptionType)
        {
            ReportedActionErrors.Add(exceptionType);
        }

        public override void ReportRefund(long refund)
        {
            Refund += refund;
        }
    }
}
