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

namespace Nethermind.Evm.Tracing
{
    public class GethLikeTxTracer : ITxTracer
    {
        private TransactionTraceEntry _traceEntry;
        private GethLikeTxTrace _trace = new GethLikeTxTrace();
        
        public bool IsTracingReceipt => true;
        bool ITxTracer.IsTracingCalls => true;
        bool ITxTracer.IsTracingStorage => true;
        bool ITxTracer.IsTracingMemory => true;
        bool ITxTracer.IsTracingInstructions => true;
        bool ITxTracer.IsTracingStack => true;
        public void MarkAsFailed()
        {
            _trace.Failed = true;
            _trace.ReturnValue = string.Empty;
        }

        public void SetReturnValue(byte[] returnValue)
        {
            _trace.ReturnValue = returnValue?.ToHexString();
        }

        public void SetGasSpent(long gasSpent)
        {
            _trace.Gas = gasSpent;
        }

        public void StartOperation(int callDepth, long gas, Instruction opcode, int programCounter)
        {
            var previousTraceEntry = _traceEntry;
            _traceEntry = new TransactionTraceEntry();
            _traceEntry.Pc = programCounter;
            _traceEntry.Operation = Enum.GetName(typeof(Instruction), opcode);
            _traceEntry.Gas = gas;
            _traceEntry.Depth = callDepth;
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

        public void UpdateMemorySize(ulong memorySize)
        {
            _traceEntry.UpdateMemorySize(memorySize);
        }

        public void ReportStorageChange(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue, long cost, long refund)
        {
            StorageTraceEntry storageTraceEntry = new StorageTraceEntry();
            storageTraceEntry.Address = address.ToString();
            storageTraceEntry.Index = storageIndex;
            storageTraceEntry.Cost = cost;
            storageTraceEntry.Refund = refund;
            storageTraceEntry.NewValue = newValue.ToHexString();
            storageTraceEntry.OldValue = currentValue.ToHexString();
            
            _trace.StorageTrace.Entries.Add(storageTraceEntry);
            byte[] bigEndian = new byte[32];
            storageIndex.ToBigEndian(bigEndian);
            _traceEntry.Storage[bigEndian.ToHexString(false)] = newValue.PadLeft(32).ToHexString(false);
        }

        public void SetOperationStack(List<string> getStackTrace)
        {
            _traceEntry.Stack = getStackTrace;
        }

        public void SetOperationMemory(List<string> getTrace)
        {
            _traceEntry.Memory = getTrace;
        }

        public GethLikeTxTrace BuildResult()
        {
            return _trace;
        }
    }
}