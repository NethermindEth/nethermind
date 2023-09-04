// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class EstimateGasTracer : TxTracer
    {
        public EstimateGasTracer()
        {
            _currentGasAndNesting.Push(new GasAndNesting(0, -1));
        }

        public override bool IsTracingReceipt => true;
        public override bool IsTracingActions => true;
        public override bool IsTracingRefunds => true;

        public byte[]? ReturnValue { get; set; }

        private long NonIntrinsicGasSpentBeforeRefund { get; set; }

        internal long GasSpent { get; set; }

        internal long IntrinsicGasAt { get; set; }

        private long TotalRefund { get; set; }

        public string? Error { get; set; }

        public byte StatusCode { get; set; }

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

        private class GasAndNesting
        {
            public GasAndNesting(long gasOnStart, int nestingLevel)
            {
                GasOnStart = gasOnStart;
                NestingLevel = nestingLevel;
            }

            public long GasOnStart { get; set; }
            public long GasUsageFromChildren { get; set; }
            public long GasLeft { get; set; }
            public int NestingLevel { get; set; }

            private long MaxGasNeeded
            {
                get
                {
                    long maxGasNeeded = GasOnStart + ExtraGasPressure - GasLeft + GasUsageFromChildren;
                    for (int i = 0; i < NestingLevel; i++)
                    {
                        maxGasNeeded = (long)Math.Ceiling(maxGasNeeded * 64m / 63);
                    }

                    return maxGasNeeded;
                }
            }

            public long AdditionalGasRequired => MaxGasNeeded - (GasOnStart - GasLeft);
            public long ExtraGasPressure { get; set; }
        }

        internal long CalculateAdditionalGasRequired(Transaction tx, IReleaseSpec releaseSpec)
        {
            long intrinsicGas = tx.GasLimit - IntrinsicGasAt;
            return _currentGasAndNesting.Peek().AdditionalGasRequired + RefundHelper.CalculateClaimableRefund(intrinsicGas + NonIntrinsicGasSpentBeforeRefund, TotalRefund, releaseSpec);
        }

        private int _currentNestingLevel = -1;

        private bool _isInPrecompile;

        private readonly Stack<GasAndNesting> _currentGasAndNesting = new();

        public override void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            if (_currentNestingLevel == -1)
            {
                IntrinsicGasAt = gas;
            }

            if (!isPrecompileCall)
            {
                _currentNestingLevel++;
                _currentGasAndNesting.Push(new GasAndNesting(gas, _currentNestingLevel));
            }
            else
            {
                _isInPrecompile = true;
            }
        }

        public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            if (!_isInPrecompile)
            {
                UpdateAdditionalGas(gas);
            }
            else
            {
                _isInPrecompile = false;
            }
        }

        public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            if (!_isInPrecompile)
            {
                UpdateAdditionalGas(gas);
            }
            else
            {
                _isInPrecompile = false;
            }
        }

        public override void ReportActionError(EvmExceptionType exceptionType)
        {
            UpdateAdditionalGas();
        }

        public void ReportActionError(EvmExceptionType exceptionType, long gasLeft)
        {
            UpdateAdditionalGas(gasLeft);
        }

        private void UpdateAdditionalGas(long? gasLeft = null)
        {
            GasAndNesting current = _currentGasAndNesting.Pop();

            if (gasLeft.HasValue)
            {
                current.GasLeft = gasLeft.Value;
            }

            _currentGasAndNesting.Peek().GasUsageFromChildren += current.AdditionalGasRequired;
            _currentNestingLevel--;

            if (_currentNestingLevel == -1)
            {
                NonIntrinsicGasSpentBeforeRefund = IntrinsicGasAt - current.GasLeft;
            }
        }

        public override void ReportRefund(long refund)
        {
            TotalRefund += refund;
        }

        public override void ReportExtraGasPressure(long extraGasPressure)
        {
            _currentGasAndNesting.Peek().ExtraGasPressure = Math.Max(_currentGasAndNesting.Peek().ExtraGasPressure, extraGasPressure);
        }
    }
}
