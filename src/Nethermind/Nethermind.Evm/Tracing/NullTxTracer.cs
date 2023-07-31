// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing
{
    public class NullTxTracer : TxTracer
    {
        public static ITxTracer Instance { get; } = new NullTxTracer();

        private const string ErrorMessage = "Null tracer should never receive any calls.";
        private NullTxTracer() { }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowInvalidOperationException() => throw new InvalidOperationException(ErrorMessage);

        public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
            => ThrowInvalidOperationException();
        public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
            => ThrowInvalidOperationException();
        public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
            => ThrowInvalidOperationException();
        public override void ReportOperationError(EvmExceptionType error)
            => ThrowInvalidOperationException();
        public override void ReportOperationRemainingGas(long gas)
            => ThrowInvalidOperationException();
        public override void SetOperationMemorySize(ulong newSize)
            => ThrowInvalidOperationException();
        public override void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
            => ThrowInvalidOperationException();
        public override void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
            => ThrowInvalidOperationException();
        public override void SetOperationStack(List<string> stackTrace)
            => ThrowInvalidOperationException();
        public override void ReportStackPush(in ReadOnlySpan<byte> stackItem)
            => ThrowInvalidOperationException();
        public override void SetOperationMemory(List<string> memoryTrace)
            => ThrowInvalidOperationException();
        public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
            => ThrowInvalidOperationException();
        public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
            => ThrowInvalidOperationException();
        public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
            => ThrowInvalidOperationException();
        public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
            => ThrowInvalidOperationException();
        public override void ReportCodeChange(Address address, byte[] before, byte[] after)
            => ThrowInvalidOperationException();
        public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
            => ThrowInvalidOperationException();
        public override void ReportAccountRead(Address address)
            => ThrowInvalidOperationException();
        public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
            => ThrowInvalidOperationException();
        public override void ReportStorageRead(in StorageCell storageCell)
            => ThrowInvalidOperationException();
        public override void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
            => ThrowInvalidOperationException();
        public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
            => ThrowInvalidOperationException();
        public override void ReportActionError(EvmExceptionType exceptionType)
            => ThrowInvalidOperationException();
        public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
            => ThrowInvalidOperationException();
        public override void ReportBlockHash(Keccak blockHash)
            => ThrowInvalidOperationException();
        public override void ReportByteCode(byte[] byteCode)
            => ThrowInvalidOperationException();
        public override void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
            => ThrowInvalidOperationException();
        public override void ReportRefund(long refund)
            => ThrowInvalidOperationException();
        public override void ReportExtraGasPressure(long extraGasPressure)
            => ThrowInvalidOperationException();
        public override void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
            => ThrowInvalidOperationException();
        public override void ReportFees(UInt256 fees, UInt256 burntFees)
            => ThrowInvalidOperationException();
    }
}
