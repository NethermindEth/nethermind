// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm.Test
{
    public class TestAllTracerWithOutput : ITxTracer
    {
        public bool IsTracingReceipt => true;
        public bool IsTracingActions => true;
        public bool IsTracingOpLevelStorage => true;
        public bool IsTracingMemory => true;
        public bool IsTracingInstructions => true;
        public bool IsTracingRefunds => true;
        public bool IsTracingCode => true;
        public bool IsTracingStack => true;
        public bool IsTracingState => true;
        public bool IsTracingStorage => true;
        public bool IsTracingBlockHash => true;
        public bool IsTracingAccess { get; set; } = true;
        public bool IsTracingFees => true;
        public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees || IsTracingEventLogs;
        public bool IsTracingEventLogs => true;

        public byte[] ReturnValue { get; set; }

        public long GasSpent { get; set; }

        public string Error { get; set; }

        public byte StatusCode { get; set; }

        public long Refund { get; set; }

        public List<EvmExceptionType> ReportedActionErrors { get; set; } = new List<EvmExceptionType>();

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
            ReturnValue = output ?? Array.Empty<byte>();
            StatusCode = Evm.StatusCode.Failure;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
        {
        }

        public void ReportOperationError(EvmExceptionType error)
        {
        }

        public void ReportOperationRemainingGas(long gas)
        {
        }

        public void SetOperationStack(List<string> stackTrace)
        {
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
        }

        public void SetOperationMemorySize(ulong newSize)
        {
        }

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
        }

        public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        {
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
        }

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
        }

        public void ReportAccountRead(Address address)
        {
        }

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
        }

        public void ReportStorageRead(in StorageCell storageCell)
        {
        }

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
        }

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
        }

        public void ReportActionError(EvmExceptionType exceptionType)
        {
            ReportedActionErrors.Add(exceptionType);
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
        }

        public void ReportBlockHash(Keccak blockHash)
        {
        }

        public void ReportByteCode(byte[] byteCode)
        {
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
        }

        public void ReportRefund(long refund)
        {
            Refund += refund;
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
        }

        public void ReportFees(UInt256 fees, UInt256 burntFees)
        {
        }

        public void ReportEvent(LogEntry logEntry)
        {
        }
    }
}
