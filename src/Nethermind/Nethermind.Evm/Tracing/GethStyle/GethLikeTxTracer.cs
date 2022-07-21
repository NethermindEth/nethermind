//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle;

public abstract class GethLikeTxTracer<TEntry> : ITxTracer where TEntry : GethTxTraceEntry
{
    protected GethLikeTxTracer(GethTraceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        IsTracingStack = !options.DisableStack;
        IsTracingMemory = !options.DisableMemory;
        IsTracingOpLevelStorage = !options.DisableStorage;
    }

    public bool IsTracingAccess => false;

    public bool IsTracingActions => false;

    public bool IsTracingBlockHash => false;

    public bool IsTracingCode => false;

    public bool IsTracingInstructions => true;

    public bool IsTracingMemory { get; protected set; }

    public bool IsTracingOpLevelStorage { get; protected set; }

    public bool IsTracingReceipt => true;

    public bool IsTracingRefunds { get; protected set; }

    public bool IsTracingStack { get; protected set; }

    public bool IsTracingState => false;

    public bool IsTracingStorage => false;

    protected TEntry? CurrentTraceEntry { get; set; }

    protected GethLikeTxTrace Trace { get; } = new();

    public virtual GethLikeTxTrace BuildResult()
    {
        if (CurrentTraceEntry != null)
            AddTraceEntry(CurrentTraceEntry);

        return Trace;
    }

    public virtual void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) { }

    public virtual void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Keccak? stateRoot = null)
    {
        Trace.Failed = true;
        Trace.ReturnValue = output ?? Array.Empty<byte>();
    }

    public virtual void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null) =>
        Trace.ReturnValue = output;

    public virtual void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells) { }

    public virtual void ReportAccountRead(Address address) { }

    public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) =>
        throw new NotSupportedException();

    public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) =>
        throw new NotSupportedException();

    public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output) => throw new NotSupportedException();

    public void ReportActionError(EvmExceptionType exceptionType) => throw new NotSupportedException();

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) => throw new NotSupportedException();

    public void ReportBlockHash(Keccak blockHash) => throw new NotSupportedException();

    public  void ReportByteCode(byte[] byteCode) { }

    public void ReportCodeChange(Address address, byte[] before, byte[] after) => throw new NotSupportedException();

    public virtual void ReportExtraGasPressure(long extraGasPressure) { }

    public virtual void ReportGasUpdateForVmTrace(long refund, long gasAvailable) { }

    public virtual void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
        // TODO?
    }

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after) => throw new NotSupportedException();

    public virtual void ReportOperationError(EvmExceptionType error) => CurrentTraceEntry.Error = GetErrorDescription(error);

    public virtual void ReportRefund(long refund) { }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) => throw new NotSupportedException();

    public virtual void ReportStackPush(in ReadOnlySpan<byte> stackItem) { }

    public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after) => throw new NotSupportedException();

    public virtual void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value) { }

    public void ReportStorageRead(StorageCell storageCell) => throw new NotSupportedException();

    public virtual void SetOperationMemory(List<string> memoryTrace) => CurrentTraceEntry.Memory = memoryTrace;

    public virtual void SetOperationMemorySize(ulong newSize) => CurrentTraceEntry.UpdateMemorySize(newSize);

    public virtual void ReportOperationRemainingGas(long gas) => CurrentTraceEntry.GasCost = CurrentTraceEntry.Gas - gas;

    public virtual void SetOperationStack(List<string> stackTrace) => CurrentTraceEntry.Stack = stackTrace;

    public virtual void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) { }

    public virtual void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge)
    {
        if (CurrentTraceEntry != null)
            AddTraceEntry(CurrentTraceEntry);

        CurrentTraceEntry = CreateTraceEntry(opcode);
        CurrentTraceEntry.Depth = depth;
        CurrentTraceEntry.Gas = gas;
        CurrentTraceEntry.Opcode = opcode.GetName(isPostMerge);
        CurrentTraceEntry.ProgramCounter = pc;
    }

    protected abstract void AddTraceEntry(TEntry entry);

    protected abstract TEntry CreateTraceEntry(Instruction opcode);

    private static string? GetErrorDescription(EvmExceptionType evmExceptionType) => evmExceptionType switch
    {
        EvmExceptionType.None => null,
        EvmExceptionType.BadInstruction => "BadInstruction",
        EvmExceptionType.StackOverflow => "StackOverflow",
        EvmExceptionType.StackUnderflow => "StackUnderflow",
        EvmExceptionType.OutOfGas => "OutOfGas",
        EvmExceptionType.InvalidSubroutineEntry => "InvalidSubroutineEntry",
        EvmExceptionType.InvalidSubroutineReturn => "InvalidSubroutineReturn",
        EvmExceptionType.InvalidJumpDestination => "BadJumpDestination",
        EvmExceptionType.AccessViolation => "AccessViolation",
        EvmExceptionType.StaticCallViolation => "StaticCallViolation",
        _ => "Error"
    };
}
