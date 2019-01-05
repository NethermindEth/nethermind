/*
 * Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Evm.Tracing
{
    public class GethLikeTxTracer : ITxTracer
    {
        private GethTxTraceEntry _traceEntry;
        private GethLikeTxTrace _trace = new GethLikeTxTrace();
        
        public bool IsTracingReceipt => true;
        bool ITxTracer.IsTracingCalls => false;
        bool ITxTracer.IsTracingOpLevelStorage => true;
        bool ITxTracer.IsTracingMemory => true;
        bool ITxTracer.IsTracingInstructions => true;
        bool ITxTracer.IsTracingStack => true;
        bool ITxTracer.IsTracingState => false;
        
        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs)
        {
            _trace.ReturnValue = output;
            _trace.Gas = gasSpent;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error)
        {
            _trace.Failed = true;
            _trace.ReturnValue = output ?? Bytes.Empty;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            var previousTraceEntry = _traceEntry;
            _traceEntry = new GethTxTraceEntry();
            _traceEntry.Pc = pc;
            _traceEntry.Operation = Enum.GetName(typeof(Instruction), opcode);
            _traceEntry.Gas = gas;
            _traceEntry.Depth = depth;
            _trace.Entries.Add(_traceEntry);
            
            if (_traceEntry.Depth > (previousTraceEntry?.Depth ?? 0))
            {
                _traceEntry.Storage = new Dictionary<string, string>();
                _trace.StoragesByDepth.Push(previousTraceEntry != null ? previousTraceEntry.Storage : new Dictionary<string, string>());
            }
            else if (_traceEntry.Depth < (previousTraceEntry?.Depth ?? 0))
            {
                if (previousTraceEntry == null)
                {
                    throw new InvalidOperationException("Unexpected missing previous trace when leaving a call.");
                }
                    
                _traceEntry.Storage = new Dictionary<string, string>(_trace.StoragesByDepth.Pop());
            }
            else
            {
                if (previousTraceEntry == null)
                {
                    throw new InvalidOperationException("Unexpected missing previous trace on continuation.");
                }
                    
                _traceEntry.Storage = new Dictionary<string, string>(previousTraceEntry.Storage);    
            }
        }

        public void SetOperationError(string error)
        {
            _traceEntry.Error = error;
        }

        public void SetOperationRemainingGas(long gas)
        {
            _traceEntry.GasCost = _traceEntry.Gas - gas;
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            _traceEntry.UpdateMemorySize(newSize);
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue)
        {
            byte[] bigEndian = new byte[32];
            storageIndex.ToBigEndian(bigEndian);
            _traceEntry.Storage[bigEndian.ToHexString(false)] = newValue.PadLeft(32).ToHexString(false);
        }

        public void ReportBalanceChange(Address address, UInt256 before, UInt256 after)
        {
            throw new NotSupportedException();
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
        }

        public void ReportNonceChange(Address address, UInt256 before, UInt256 after)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageChange(StorageAddress storageAddress, byte[] before, byte[] after)
        {
            throw new NotSupportedException();
        }

        public void ReportCall(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType)
        {
            throw new NotSupportedException();
        }

        public void ReportCallEnd(long gas, byte[] output)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            _traceEntry.Stack = stackTrace;
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            _traceEntry.Memory = memoryTrace;
        }

        public GethLikeTxTrace BuildResult()
        {
            return _trace;
        }
    }
}