// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.State.Tracing;

namespace Nethermind.Evm.Tracing.GethStyle
{
    public abstract class GethLikeTxTracer<TEntry> : ITxTracer where TEntry : GethTxTraceEntry
    {
        protected GethLikeTxTracer(GethTraceOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            IsTracingFullMemory = options.EnableMemory;
            IsTracingOpLevelStorage = !options.DisableStorage;
            IsTracingStack = !options.DisableStack;
        }

        bool IStateTracer.IsTracingState => false;
        bool IStorageTracer.IsTracingStorage => false;
        public bool IsTracingReceipt => true;
        public bool IsTracingActions => false;
        public bool IsTracingOpLevelStorage { get; protected set; }
        public bool IsTracingMemory { get; protected set; }
        protected bool IsTracingFullMemory { get; }
        public bool IsTracingInstructions => true;
        public bool IsTracingRefunds { get; protected set; }
        public bool IsTracingCode => false;
        public bool IsTracingStack { get; }
        public bool IsTracingBlockHash => false;
        public bool IsTracingAccess => false;
        public bool IsTracingFees => false;
        public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingFullMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees;
        protected TEntry? CurrentTraceEntry { get; set; }
        protected GethLikeTxTrace Trace { get; } = new();

        public virtual void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            Trace.ReturnValue = output;
        }

        public virtual void MarkAsFailed(Address recipient, long gasSpent, byte[]? output, string error, Keccak? stateRoot = null)
        {
            Trace.Failed = true;
            Trace.ReturnValue = output ?? Array.Empty<byte>();
        }

        public virtual void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge)
        {
            if (CurrentTraceEntry is not null)
                AddTraceEntry(CurrentTraceEntry);

            CurrentTraceEntry = CreateTraceEntry(opcode);
            CurrentTraceEntry.Depth = depth;
            CurrentTraceEntry.Gas = gas;
            CurrentTraceEntry.Opcode = opcode.GetName(isPostMerge);
            CurrentTraceEntry.ProgramCounter = pc;
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            CurrentTraceEntry.Error = GetErrorDescription(error);
        }

        private string? GetErrorDescription(EvmExceptionType evmExceptionType)
        {
            return evmExceptionType switch
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

        public void ReportOperationRemainingGas(long gas)
        {
            CurrentTraceEntry.GasCost = CurrentTraceEntry.Gas - gas;
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            CurrentTraceEntry.UpdateMemorySize(newSize);
        }

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
        }

        public virtual void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue) { }

        public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        {

        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            throw new NotSupportedException();
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
        }

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageRead(in StorageCell storageCell)
        {
            throw new NotSupportedException();
        }

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            throw new NotSupportedException();
        }

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            throw new NotSupportedException();
        }

        public void ReportActionError(EvmExceptionType exceptionType)
        {
            throw new NotSupportedException();
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            throw new NotSupportedException();
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
        }

        public virtual void ReportRefund(long refund) { }

        public void ReportExtraGasPressure(long extraGasPressure) { }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            throw new NotImplementedException();
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            CurrentTraceEntry.Stack = stackTrace;
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
        }

        public void SetOperationMemory(IEnumerable<string> memoryTrace)
        {
            if (IsTracingFullMemory)
                CurrentTraceEntry.Memory = memoryTrace.ToList();
        }

        public void ReportFees(UInt256 fees, UInt256 burntFees)
        {
            throw new NotImplementedException();
        }

        public virtual GethLikeTxTrace BuildResult()
        {
            if (CurrentTraceEntry is not null)
                AddTraceEntry(CurrentTraceEntry);

            return Trace;
        }

        protected abstract void AddTraceEntry(TEntry entry);

        protected abstract TEntry CreateTraceEntry(Instruction opcode);
    }
}
