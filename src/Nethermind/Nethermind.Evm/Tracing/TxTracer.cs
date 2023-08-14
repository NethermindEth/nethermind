// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public class TxTracer : ITxTracer
{
    [SuppressMessage("ReSharper", "VirtualMemberCallInConstructor")]
    protected TxTracer()
    {
        IsTracing = IsTracingReceipt
                    || IsTracingActions
                    || IsTracingOpLevelStorage
                    || IsTracingMemory
                    || IsTracingInstructions
                    || IsTracingRefunds
                    || IsTracingCode
                    || IsTracingStack
                    || IsTracingBlockHash
                    || IsTracingAccess
                    || IsTracingFees;
    }
    public bool IsTracing { get; protected set; }
    public virtual bool IsTracingState { get; protected set; }
    public virtual bool IsTracingReceipt { get; protected set; }
    public virtual bool IsTracingActions { get; protected set; }
    public virtual bool IsTracingOpLevelStorage { get; protected set; }
    public virtual bool IsTracingMemory { get; protected set; }
    public virtual bool IsTracingInstructions { get; protected set; }
    public virtual bool IsTracingRefunds { get; protected set; }
    public virtual bool IsTracingCode { get; protected set; }
    public virtual bool IsTracingStack { get; protected set; }
    public virtual bool IsTracingBlockHash { get; protected set; }
    public virtual bool IsTracingAccess { get; protected set; }
    public virtual bool IsTracingFees { get; protected set; }
    public virtual bool IsTracingStorage { get; protected set; }
    public virtual void ReportBalanceChange(Address address, UInt256? before, UInt256? after) { }
    public virtual void ReportCodeChange(Address address, byte[]? before, byte[]? after) { }
    public virtual void ReportNonceChange(Address address, UInt256? before, UInt256? after) { }
    public virtual void ReportAccountRead(Address address) { }
    public virtual void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) { }
    public virtual void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) { }
    public virtual void ReportStorageRead(in StorageCell storageCell) { }
    public virtual void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null) { }
    public virtual void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null) { }
    public virtual void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false) { }
    public virtual void ReportOperationError(EvmExceptionType error) { }
    public virtual void ReportOperationRemainingGas(long gas) { }
    public virtual void SetOperationStack(List<string> stackTrace) { }
    public virtual void ReportStackPush(in ReadOnlySpan<byte> stackItem) { }
    public virtual void SetOperationMemory(IEnumerable<string> memoryTrace) { }
    public virtual void SetOperationMemorySize(ulong newSize) { }
    public virtual void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data) { }
    public virtual void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) { }
    public virtual void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) { }
    public virtual void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) { }
    public virtual void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) { }
    public virtual void ReportActionEnd(long gas, ReadOnlyMemory<byte> output) { }
    public virtual void ReportActionError(EvmExceptionType evmExceptionType) { }
    public virtual void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) { }
    public virtual void ReportBlockHash(Keccak blockHash) { }
    public virtual void ReportByteCode(byte[] byteCode) { }
    public virtual void ReportGasUpdateForVmTrace(long refund, long gasAvailable) { }
    public virtual void ReportRefund(long refund) { }
    public virtual void ReportExtraGasPressure(long extraGasPressure) { }
    public virtual void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells) { }
    public virtual void ReportFees(UInt256 fees, UInt256 burntFees) { }
}
