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
        private ParityTraceAction _currentCall;

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
        }

        private void PushCall(ParityTraceAction call)
        {
            if (_currentCall != null)
            {
                call.TraceAddress = new int[_currentCall.TraceAddress.Length + 1];
                for (int i = 0; i < _currentCall.TraceAddress.Length; i++)
                {
                    call.TraceAddress[i] = _currentCall.TraceAddress[i];
                }

                call.TraceAddress[_currentCall.TraceAddress.Length] = _currentCall.Subtraces.Count;
                _currentCall.Subtraces.Add(call);
            }
            else
            {
                _trace.Action = call;
                _trace.Type = _trace.Action.CallType;
                call.TraceAddress = Array.Empty<int>();
            }

            _callStack.Push(call);
            _currentCall = call;
        }

        private void PopCall()
        {
            _callStack.Pop();
            _currentCall = _callStack.Count == 0 ? null : _callStack.Peek();
        }

        public bool IsTracingReceipt => true;
        public bool IsTracingCalls => true;
        public bool IsTracingStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingStack => false;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs)
        {
            if (_currentCall != null)
            {
                throw new InvalidOperationException($"Closing trace at level {_currentCall.TraceAddress.Length}");
            }

            _trace.Action.To = recipient;
            _trace.Action.Result = new ParityTraceResult {Output = output, GasUsed = (long) gasSpent};
        }

        public void MarkAsFailed(Address recipient, long gasSpent)
        {
            if (_currentCall.TraceAddress.Length != 0)
            {
                throw new InvalidOperationException($"Closing trace at level {_currentCall.TraceAddress.Length}");
            }

            _trace.Action.To = recipient;
            _trace.Action.Result = new ParityTraceResult {Output = Bytes.Empty, GasUsed = (long) gasSpent};
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc) => throw new NotSupportedException();

        public void SetOperationError(string error) => throw new NotSupportedException();

        public void SetOperationRemainingGas(long gas) => throw new NotSupportedException();

        public void SetOperationStack(List<string> stackTrace) => throw new NotSupportedException();

        public void SetOperationMemory(List<string> memoryTrace) => throw new NotSupportedException();

        public void SetOperationMemorySize(ulong newSize) => throw new NotSupportedException();

        public void ReportStorageChange(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue, long cost, long refund) => throw new NotSupportedException();

        public void ReportCall(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType)
        {
            ParityTraceAction action = new ParityTraceAction();
            action.From = @from;
            action.To = to;
            action.Value = value;
            action.Input = input;
            action.Gas = gas;
            action.CallType = (callType == ExecutionType.Create || callType == ExecutionType.DirectCreate) ? "init" : "call";

            PushCall(action);
        }

        public void ReportCallEnd(long gas, byte[] output)
        {
            _currentCall.Result.Output = output;
            _currentCall.Result.GasUsed = _currentCall.Gas - gas;
            PopCall();
        }
    }
}