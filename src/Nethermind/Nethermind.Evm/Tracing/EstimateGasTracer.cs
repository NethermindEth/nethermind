// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing
{
    public class EstimateGasTracer : ITxTracer
    {
        public EstimateGasTracer()
        {
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
        public bool IsTracingStorage => false;
        public bool IsTracingBlockHash => false;
        public bool IsTracingAccess => false;
        public bool IsTracingFees => false;
        public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees || IsTracingEventLogs;
        public bool IsTracingEventLogs => false;

        public byte[] ReturnValue { get; set; }

        internal long NonIntrinsicGasSpentBeforeRefund { get; set; }

        internal long GasSpent { get; set; }

        internal long IntrinsicGasAt { get; set; }

        internal long TotalRefund { get; set; }

        public string Error { get; set; }

        public byte StatusCode { get; set; }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            GasSpent = gasSpent;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Keccak? stateRoot = null)
        {
            GasSpent = gasSpent;
            Error = error;
            ReturnValue = output ?? Array.Empty<byte>();
            StatusCode = Evm.StatusCode.Failure;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
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

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
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

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
            throw new NotSupportedException();
        }

        public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
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

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageRead(in StorageCell storageCell)
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

        private Stack<GasAndNesting> _currentGasAndNesting = new();

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
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

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
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

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
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

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            throw new NotImplementedException();
        }

        public void ReportFees(UInt256 fees, UInt256 burntFees)
        {
            throw new NotImplementedException();
        }

        public void ReportEvent(LogEntry logEntry)
        {
            throw new NotImplementedException();
        }
    }
}
