// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public class NullTxTracer : TxTracer
{
    public static ITxTracer Instance { get; } = new NullTxTracer();

    private const string ErrorMessage = "Null tracer should never receive any calls.";
    private NullTxTracer() { }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowInvalidOperationException() => throw new InvalidOperationException(ErrorMessage);

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        => ThrowInvalidOperationException();
    public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        => ThrowInvalidOperationException();
    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
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

    public override void SetOperationStack(TraceStack stack)
        => ThrowInvalidOperationException();

    public override void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        => ThrowInvalidOperationException();

    public override void SetOperationMemory(TraceMemory memoryTrace)
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

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        => ThrowInvalidOperationException();

    public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        => ThrowInvalidOperationException();

    public override void ReportActionError(EvmExceptionType exceptionType)
        => ThrowInvalidOperationException();

    public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        => ThrowInvalidOperationException();
    public override void ReportBlockHash(Hash256 blockHash)
        => ThrowInvalidOperationException();
    public override void ReportByteCode(ReadOnlyMemory<byte> byteCode)
        => ThrowInvalidOperationException();

    public override void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        => ThrowInvalidOperationException();

    public override void ReportRefund(long refund)
        => ThrowInvalidOperationException();

    public override void ReportExtraGasPressure(long extraGasPressure)
        => ThrowInvalidOperationException();

    public override void ReportAccess(IReadOnlyCollection<Address> accessedAddresses, IReadOnlyCollection<StorageCell> accessedStorageCells)
        => ThrowInvalidOperationException();

    public override void ReportFees(UInt256 fees, UInt256 burntFees)
        => ThrowInvalidOperationException();
}
