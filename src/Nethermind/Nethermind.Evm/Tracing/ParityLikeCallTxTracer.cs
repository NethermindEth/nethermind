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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Tracing
{
    public class ParityLikeCallTxTracer : ITxTracer
    {
        private ParityLikeCallTxTrace _trace;
        private Stack<ParityTraceAction> _callStack = new Stack<ParityTraceAction>();
        private ParityTraceAction _currentCall = new ParityTraceAction();

        public ParityLikeCallTxTrace BuildResult()
        {
            return _trace;
        }

        public ParityLikeCallTxTracer(Block block, Transaction tx)
        {
            _trace = new ParityLikeCallTxTrace();
            _trace.TransactionHash = tx.Hash;
            _trace.TransactionPosition = block.Transactions.Select((t, ix) => (t, ix)).Where(p => p.t.Hash == tx.Hash).Select((t, ix) => ix).SingleOrDefault();
            _trace.BlockNumber = block.Number;
            _trace.BlockHash = block.Hash;
            
            _trace.Action = new ParityTraceAction();
            _trace.Action.TraceAddress = Array.Empty<int>();
            _trace.Action.Gas = (long)tx.GasLimit;
            _trace.Action.Value = tx.Value;
            _trace.Action.Input = tx.Data;
            _trace.Action.From = tx.SenderAddress;
            _trace.Action.CallType = tx.IsMessageCall ? "call" : "init";
            
            PushCall(_trace.Action);

            _trace.Type = _trace.Action.CallType;
        }

        private void PushCall(ParityTraceAction parityTraceAction)
        {
            _callStack.Push(parityTraceAction);
            _currentCall = parityTraceAction;
        }
        
        private void PopCall()
        {
            _currentCall = _callStack.Pop();
        }

        public bool IsTracingReceipt => true;
        public bool IsTracingCalls => true;
        public bool IsTracingStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingStack => false;
        
        [Todo(Improve.MissingFunctionality, "Need to remove intrinsic gas value from gas spent")]
        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] returnValue, LogEntry[] logs)
        {
            if (_currentCall.TraceAddress.Length != 0)
            {
                throw new InvalidOperationException($"Closing trace at level {_currentCall.TraceAddress.Length}");
            }
            
            _trace.Action.To = recipient;
            _trace.Action.Result = new ParityTraceResult{Output = returnValue, GasUsed = (long)gasSpent};
        }

        public void MarkAsFailed(Address recipient, long gasSpent)
        {
            if (_currentCall.TraceAddress.Length != 0)
            {
                throw new InvalidOperationException($"Closing trace at level {_currentCall.TraceAddress.Length}");
            }
            
            _trace.Action.To = recipient;
            _trace.Action.Result = new ParityTraceResult{Output = Bytes.Empty, GasUsed = (long)gasSpent};
        }

        public void StartOperation(int callDepth, long gas, Instruction opcode, int programCounter)
        {
            throw new System.NotImplementedException();
        }

        public void SetOperationError(string error)
        {
            throw new System.NotImplementedException();
        }

        public void SetOperationRemainingGas(long gas)
        {
            throw new System.NotImplementedException();
        }

        public void SetOperationStack(List<string> getStackTrace)
        {
            throw new System.NotImplementedException();
        }

        public void SetOperationMemory(List<string> getTrace)
        {
            throw new System.NotImplementedException();
        }

        public void UpdateMemorySize(ulong memorySize)
        {
            throw new System.NotImplementedException();
        }

        public void ReportStorageChange(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue, long cost, long refund)
        {
            throw new System.NotImplementedException();
        }
    }
}