/*
 * Copyright (c) 2021 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.State.Test.Runner
{
    public class StateTestTxTracer : ITxTracer
    {
        private StateTestTxTraceEntry _traceEntry;
        private StateTestTxTrace _trace = new();
        private bool _gasAlreadySetForCurrentOp;

        public bool IsTracingReceipt => true;
        bool ITxTracer.IsTracingActions => false;
        public bool IsTracingOpLevelStorage => true;
        public bool IsTracingMemory { get; set; } = true;
        bool ITxTracer.IsTracingInstructions => true;
        public bool IsTracingRefunds { get; } = false;
        public bool IsTracingCode => false;
        public bool IsTracingStack { get; set; } = true;
        bool IStateTracer.IsTracingState => false;
        bool IStorageTracer.IsTracingStorage => false;
        public bool IsTracingBlockHash { get; } = false;
        public bool IsTracingAccess { get; } = false;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak stateRoot = null)
        {
            _trace.Result.Output = output;
            _trace.Result.GasUsed = gasSpent;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak stateRoot = null)
        {
            _trace.Result.Error = _traceEntry?.Error ?? error;
            _trace.Result.Output = output ?? Bytes.Empty;
            _trace.Result.GasUsed = gasSpent;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            _gasAlreadySetForCurrentOp = false;
            _traceEntry = new StateTestTxTraceEntry();
            _traceEntry.Pc = pc;
            _traceEntry.Operation = (byte)opcode;
            _traceEntry.OperationName = Enum.GetName(typeof(Instruction), opcode);
            _traceEntry.Gas = gas;
            _traceEntry.Depth = depth;
            _trace.Entries.Add(_traceEntry);
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            _traceEntry.Error = GetErrorDescription(error);
        }
        
        private static string? GetErrorDescription(EvmExceptionType evmExceptionType)
        {
            return evmExceptionType switch
            {
                EvmExceptionType.None => null,
                EvmExceptionType.BadInstruction => "BadInstruction",
                EvmExceptionType.StackOverflow => "StackOverflow",
                EvmExceptionType.StackUnderflow => "StackUnderflow",
                EvmExceptionType.OutOfGas => "OutOfGas",
                EvmExceptionType.InvalidJumpDestination => "BadJumpDestination",
                EvmExceptionType.AccessViolation => "AccessViolation",
                EvmExceptionType.StaticCallViolation => "StaticCallViolation",
                _ => "Error"
            };
        }

        public void ReportOperationRemainingGas(long gas)
        {
            if (!_gasAlreadySetForCurrentOp)
            {
                _gasAlreadySetForCurrentOp = true;
                _traceEntry.GasCost = _traceEntry.Gas - gas;
            }
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            _traceEntry.UpdateMemorySize(newSize);
            int diff = (int) _traceEntry.MemSize * 2 - (_traceEntry.Memory.Length - 2);
            if (diff > 0)
            {
                _traceEntry.Memory += new string('0', diff);
            }

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
            throw new NotImplementedException();
        }

        public void ReportStorageChange(StorageCell storageAddress, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageRead(StorageCell storageCell)
        {
            throw new NotImplementedException();
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
            throw new NotImplementedException();
        }

        public void ReportByteCode(byte[] byteCode)
        {
            throw new NotSupportedException();
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
        }

        public void ReportRefundForVmTrace(long refund, long gasAvailable)
        {
        }

        public void ReportRefund(long refund)
        {
            _traceEntry.Refund = (int)refund;
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            throw new NotImplementedException();
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            throw new NotImplementedException();
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            _traceEntry.Stack = new List<string>();
            foreach (string s in stackTrace)
            {
                ReadOnlySpan<char> inProgress = s.AsSpan();
                if (s.StartsWith("0x"))
                {
                    inProgress = inProgress.Slice(2);
                }
                
                inProgress = inProgress.TrimStart('0');

                _traceEntry.Stack.Add(inProgress.Length == 0 ? "0x0" : "0x" + inProgress.ToString());
            }
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            _traceEntry.Memory = string.Concat("0x", string.Join("", memoryTrace.Select(mt => mt.Replace("0x", string.Empty))));
        }

        public StateTestTxTrace BuildResult()
        {
            return _trace;
        }
    }
}
