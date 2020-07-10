//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class EstimateGasTracer : ITxTracer
    {
        private readonly CancellationToken _cancellationToken; 

        public EstimateGasTracer(CancellationToken cancellationToken = default(CancellationToken))
        {
            _cancellationToken = cancellationToken;
            _currentGasAndNesting.Push(new GasAndNesting(0, -1));
        }

        public bool IsTracingReceipt => true;
        public bool IsTracingActions => true;
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingRefunds => true;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingState => false;
        public bool IsTracingBlockHash => false;

        public byte[] ReturnValue { get; set; }

        internal long NonIntrinsicGasSpentBeforeRefund { get; set; }

        internal long GasSpent { get; set; }

        internal long IntrinsicGasAt { get; set; }

        internal long TotalRefund { get; set; }

        public string Error { get; set; }

        public byte StatusCode { get; set; }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            GasSpent = gasSpent;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            GasSpent = gasSpent;
            Error = error;
            ReturnValue = output ?? Bytes.Empty;
            StatusCode = Evm.StatusCode.Failure;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            throw new NotSupportedException();
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            throw new NotSupportedException();
        }

        public void ReportOperationRemainingGas(long gas)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            throw new NotSupportedException();
        }

        public void ReportStackPush(Span<byte> stackItem)
        {
            throw new NotSupportedException();
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            throw new NotSupportedException();
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            throw new NotSupportedException();
        }

        public void ReportMemoryChange(long offset, Span<byte> data)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageChange(Span<byte> key, Span<byte> value)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue)
        {
            throw new NotSupportedException();
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
        }

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            throw new NotSupportedException();
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            throw new NotSupportedException();
        }

        public void ReportAccountRead(Address address)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
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
                        maxGasNeeded = (long) Math.Ceiling(maxGasNeeded * 64m / 63);
                    }

                    return maxGasNeeded;
                }
            }

            public long AdditionalGasRequired => MaxGasNeeded - (GasOnStart - GasLeft);
            public long ExtraGasPressure { get; set; }
        }

        internal long CalculateAdditionalGasRequired(Transaction tx)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long intrinsicGas = tx.GasLimit - IntrinsicGasAt;
            return _currentGasAndNesting.Peek().AdditionalGasRequired + RefundHelper.CalculateClaimableRefund(intrinsicGas + NonIntrinsicGasSpentBeforeRefund, TotalRefund);
        }

        public long CalculateEstimate(Transaction tx)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long intrinsicGas = tx.GasLimit - IntrinsicGasAt;
            return Math.Max(intrinsicGas, GasSpent + CalculateAdditionalGasRequired(tx));
        }

        private int _currentNestingLevel = -1;

        private bool _isInPrecompile = false;

        private Stack<GasAndNesting> _currentGasAndNesting = new Stack<GasAndNesting>();

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType, bool isPrecompileCall = false)
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

        public void ReportActionEnd(long gas, byte[] output)
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

        public void ReportActionEnd(long gas, Address deploymentAddress, byte[] deployedCode)
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

        public void ReportActionError(EvmExceptionType exceptionType)
        {
            UpdateAdditionalGas(_currentGasAndNesting.Peek().GasLeft);
        }

        private void UpdateAdditionalGas(long gas)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var current = _currentGasAndNesting.Pop();
            current.GasLeft = gas;
            _currentGasAndNesting.Peek().GasUsageFromChildren += current.AdditionalGasRequired;
            _currentNestingLevel--;

            if (_currentNestingLevel == -1)
            {
                NonIntrinsicGasSpentBeforeRefund = IntrinsicGasAt - current.GasLeft;
            }
        }

        public void ReportBlockHash(Keccak blockHash)
        {
            throw new NotSupportedException();
        }

        public void ReportByteCode(byte[] byteCode)
        {
            throw new NotSupportedException();
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
            throw new NotSupportedException();
        }

        public void ReportRefund(long refund)
        {
            TotalRefund += refund;
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            _currentGasAndNesting.Peek().ExtraGasPressure = Math.Max(_currentGasAndNesting.Peek().ExtraGasPressure, extraGasPressure);
        }
    }
}