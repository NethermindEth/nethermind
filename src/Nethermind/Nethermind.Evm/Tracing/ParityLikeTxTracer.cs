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
using Nethermind.Store;

namespace Nethermind.Evm.Tracing
{
    public class ParityLikeTxTracer : ITxTracer
    {
        private readonly Transaction _tx;
        private ParityLikeTxTrace _trace;
        private Stack<ParityTraceAction> _callStack = new Stack<ParityTraceAction>();
        private ParityTraceAction _currentCall;

        public ParityLikeTxTrace BuildResult()
        {
            return _trace;
        }

        public ParityLikeTxTracer(Block block, Transaction tx, ParityTraceTypes parityTraceTypes)
        {
            _tx = tx;
            _trace = new ParityLikeTxTrace();
            _trace.TransactionHash = tx?.Hash;
            _trace.TransactionPosition = tx == null ? (int?)null : block.Transactions.Select((t, ix) => (t, ix)).Where(p => p.t.Hash == tx.Hash).Select((t, ix) => ix).SingleOrDefault();
            _trace.BlockNumber = block.Number;
            _trace.BlockHash = block.Hash;

            if ((parityTraceTypes & ParityTraceTypes.StateDiff) != 0)
            {
                IsTracingState = true;
                _trace.StateChanges = new Dictionary<Address, ParityAccountStateChange>();
            }

            if ((parityTraceTypes & ParityTraceTypes.Trace) != 0)
            {
                IsTracingCalls = true;
                IsTracingReceipt = true;
            }

            if ((parityTraceTypes & ParityTraceTypes.VmTrace) != 0)
            {
                throw new NotImplementedException();
            }
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

        public bool IsTracingReceipt { get; }
        public bool IsTracingCalls { get; }
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingStack => false;
        public bool IsTracingState { get; }

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
            if (_currentCall != null)
            {
                throw new InvalidOperationException($"Closing trace at level {_currentCall.TraceAddress.Length}");
            }

            // quick tx fail (before execution)
            if (_trace.Action == null)
            {
                _trace.Action = new ParityTraceAction();
                _trace.Action.From = _tx.SenderAddress;
                _trace.Action.To = _tx.To;
                _trace.Action.Value = _tx.Value;
                _trace.Action.Input = _tx.Data;
                _trace.Action.Gas = (long)_tx.GasLimit;
                _trace.Action.CallType = _tx.IsMessageCall ? "call" : "init";
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

        public void SetOperationStorage(Address address, UInt256 storageIndex, byte[] newValue, byte[] currentValue) => throw new NotSupportedException();

        public void ReportBalanceChange(Address address, UInt256 before, UInt256 after)
        {
            if (!_trace.StateChanges.ContainsKey(address))
            {
                _trace.StateChanges[address] = new ParityAccountStateChange();
            }
            else
            {
                before = _trace.StateChanges[address].Balance?.Before ?? before;
            }

            _trace.StateChanges[address].Balance = new ParityStateChange<UInt256>(before, after);
        }

        public void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            if (!_trace.StateChanges.ContainsKey(address))
            {
                _trace.StateChanges[address] = new ParityAccountStateChange();
            }
            else
            {
                before = _trace.StateChanges[address].Code?.Before ?? before;
            }

            _trace.StateChanges[address].Code = new ParityStateChange<byte[]>(before, after);
        }

        public void ReportNonceChange(Address address, UInt256 before, UInt256 after)
        {
            if (!_trace.StateChanges.ContainsKey(address))
            {
                _trace.StateChanges[address] = new ParityAccountStateChange();
            }
            else
            {
                before = _trace.StateChanges[address].Nonce?.Before ?? before;
            }

            _trace.StateChanges[address].Nonce = new ParityStateChange<UInt256>(before, after);
        }

        public void ReportStorageChange(StorageAddress storageAddress, byte[] before, byte[] after)
        {
            Dictionary<UInt256, ParityStateChange<byte[]>> storage = null;
            if (!_trace.StateChanges.ContainsKey(storageAddress.Address))
            {
                _trace.StateChanges[storageAddress.Address] = new ParityAccountStateChange();
            }

            storage = _trace.StateChanges[storageAddress.Address].Storage;
            if (storage == null)
            {
                storage = _trace.StateChanges[storageAddress.Address].Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>();
            }

            if (storage.ContainsKey(storageAddress.Index))
            {
                before = storage[storageAddress.Index].Before ?? before;
            }

            storage[storageAddress.Index] = new ParityStateChange<byte[]>(before, after);
        }

        public void ReportCall(long gas, UInt256 value, Address @from, Address to, byte[] input, ExecutionType callType)
        {
            ParityTraceAction action = new ParityTraceAction();
            action.From = @from;
            action.To = to;
            action.Value = value;
            action.Input = input;
            action.Gas = gas;
            action.CallType = GetCallType(callType);

            PushCall(action);
        }

        private string GetCallType(ExecutionType executionType)
        {
            switch (executionType)
            {
                case ExecutionType.Transaction:
                    return "call";
                case ExecutionType.Create:
                    return "init";
                case ExecutionType.Call:
                    return "call";
                case ExecutionType.DelegateCall:
                    return "delegateCall";
                case ExecutionType.StaticCall:
                    return "staticCall";
                case ExecutionType.CallCode:
                    return "callCode";
                default:
                    throw new NotImplementedException($"Parity trace call type is undefined for {Enum.GetName(typeof(ExecutionType), executionType)}");
            }
        }

        public void ReportCallEnd(long gas, byte[] output)
        {
            _currentCall.Result.Output = output;
            _currentCall.Result.GasUsed = _currentCall.Gas - gas;
            PopCall();
        }
    }
}