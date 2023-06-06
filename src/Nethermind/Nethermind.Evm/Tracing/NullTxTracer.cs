// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class NullTxTracer : ITxTracer
    {
        public static ITxTracer Instance { get; } = new NullTxTracer();

        private const string ErrorMessage = "Null tracer should never receive any calls.";
        private NullTxTracer() { }

        public bool IsTracingReceipt => false;
        public bool IsTracingActions => false;
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingRefunds => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingState => false;
        public bool IsTracingStorage => false;
        public bool IsTracingBlockHash => false;
        public bool IsTracingAccess => false;
        public bool IsTracingFees => false;
        public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees || IsTracingEventLogs;
        public bool IsTracingEventLogs => false;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
            => throw new InvalidOperationException(ErrorMessage);

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
            => throw new InvalidOperationException(ErrorMessage);

        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportOperationError(EvmExceptionType error)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportOperationRemainingGas(long gas)
            => throw new InvalidOperationException(ErrorMessage);

        public void SetOperationMemorySize(ulong newSize)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
            => throw new InvalidOperationException(ErrorMessage);
        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
            => throw new InvalidOperationException(ErrorMessage);

        public void SetOperationStack(List<string> stackTrace)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
            => throw new InvalidOperationException(ErrorMessage);

        public void SetOperationMemory(List<string> memoryTrace)
            => throw new InvalidOperationException(ErrorMessage);

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
            => throw new InvalidOperationException(ErrorMessage);

        public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportAccountRead(Address address)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
            => throw new InvalidOperationException(ErrorMessage);
        public void ReportStorageRead(in StorageCell storageCell)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
            => throw new InvalidOperationException(ErrorMessage);
        public void ReportActionError(EvmExceptionType exceptionType)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
            => throw new InvalidOperationException(ErrorMessage);
        public void ReportBlockHash(Keccak blockHash)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportByteCode(byte[] byteCode)
            => throw new InvalidOperationException(ErrorMessage);
        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
            => throw new InvalidOperationException(ErrorMessage);
        public void ReportRefund(long refund)
            => throw new InvalidOperationException(ErrorMessage);
        public void ReportExtraGasPressure(long extraGasPressure)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportFees(UInt256 fees, UInt256 burntFees)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportEvent(LogEntry logEntry)
            => throw new InvalidOperationException(ErrorMessage);
    }
}
