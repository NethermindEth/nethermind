// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    public class ParityLikeTxTracer : TxTracer
    {
        private readonly Transaction? _tx;
        private readonly ParityTraceTypes _parityTraceTypes;
        private readonly ParityLikeTxTrace _trace;

        private readonly Stack<ParityTraceAction> _actionStack = new();
        private ParityTraceAction? _currentAction;

        private ParityVmOperationTrace? _currentOperation;
        private readonly List<byte[]> _currentPushList = new();

        private readonly Stack<(ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops)> _vmTraceStack = new();
        private (ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops) _currentVmTrace;

        private bool _treatGasParityStyle; // strange cost calculation from parity
        private bool _gasAlreadySetForCurrentOp; // workaround for jump destination errors

        public ParityLikeTxTracer(Block block, Transaction? tx, ParityTraceTypes parityTraceTypes)
        {
            _parityTraceTypes = parityTraceTypes;

            _tx = tx;
            _trace = new ParityLikeTxTrace
            {
                TransactionHash = tx?.Hash,
                TransactionPosition = tx is null ? null : Array.IndexOf(block.Transactions!, tx),
                BlockNumber = block.Number,
                BlockHash = block.Hash!
            };

            if ((_parityTraceTypes & ParityTraceTypes.StateDiff) != 0)
            {
                IsTracingState = true;
                IsTracingStorage = true;
                _trace.StateChanges = new Dictionary<Address, ParityAccountStateChange>();
            }

            if ((_parityTraceTypes & ParityTraceTypes.Trace) != 0)
            {
                IsTracingActions = true;
                IsTracingReceipt = true;
            }

            if ((_parityTraceTypes & ParityTraceTypes.VmTrace) != 0)
            {
                IsTracingActions = true;
                IsTracingInstructions = true;
                IsTracingCode = true;
                IsTracingReceipt = true;
            }
        }

        public sealed override bool IsTracingActions { get; protected set; }
        public sealed override bool IsTracingReceipt { get; protected set; }
        public sealed override bool IsTracingInstructions { get; protected set; }
        public sealed override bool IsTracingCode { get; protected set; }
        public sealed override bool IsTracingState { get; protected set; }
        public sealed override bool IsTracingStorage { get; protected set; }

        private static string GetCallType(ExecutionType executionType)
        {
            switch (executionType)
            {
                case ExecutionType.Transaction:
                    return "call";
                case ExecutionType.Create:
                    return "create";
                case ExecutionType.Create2:
                    return "create";
                case ExecutionType.Call:
                    return "call";
                case ExecutionType.DelegateCall:
                    return "delegatecall";
                case ExecutionType.StaticCall:
                    return "staticcall";
                case ExecutionType.CallCode:
                    return "callcode";
                default:
                    throw new NotSupportedException($"Parity trace call type is undefined for {executionType}");
            }
        }

        private string GetActionType(ExecutionType executionType)
        {
            switch (executionType)
            {
                case ExecutionType.Transaction:
                    return "call";
                case ExecutionType.Create:
                    return "create";
                case ExecutionType.Create2:
                    return "create";
                case ExecutionType.Call:
                    return "call";
                case ExecutionType.DelegateCall:
                    return "call";
                case ExecutionType.StaticCall:
                    return "call";
                case ExecutionType.CallCode:
                    return "call";
                default:
                    return "call";
            }
        }

        private static string? GetErrorDescription(EvmExceptionType evmExceptionType)
        {
            switch (evmExceptionType)
            {
                case EvmExceptionType.None:
                    return null;
                case EvmExceptionType.BadInstruction:
                    return "Bad instruction";
                case EvmExceptionType.StackOverflow:
                    return "Stack overflow";
                case EvmExceptionType.StackUnderflow:
                    return "Stack underflow";
                case EvmExceptionType.OutOfGas:
                    return "Out of gas";
                case EvmExceptionType.InvalidSubroutineEntry:
                    return "Invalid subroutine entry";
                case EvmExceptionType.InvalidSubroutineReturn:
                    return "Invalid subroutine return";
                case EvmExceptionType.InvalidJumpDestination:
                    return "Bad jump destination";
                case EvmExceptionType.AccessViolation:
                    return "Access violation";
                case EvmExceptionType.StaticCallViolation:
                    return "Static call violation";
                case EvmExceptionType.Revert:
                    return "Reverted";
                default:
                    return "Error";
            }
        }

        public ParityLikeTxTrace BuildResult()
        {
            if ((_parityTraceTypes & ParityTraceTypes.Trace) == ParityTraceTypes.None)
            {
                _trace.Action = null;
            }

            return _trace;
        }

        private void PushAction(ParityTraceAction action)
        {
            if (_currentAction is not null)
            {
                action.TraceAddress = new int[_currentAction!.TraceAddress!.Length + 1];
                for (int i = 0; i < _currentAction.TraceAddress.Length; i++)
                {
                    action.TraceAddress[i] = _currentAction.TraceAddress[i];
                }

                action.TraceAddress[_currentAction.TraceAddress.Length] = _currentAction.Subtraces.Count(st => st.IncludeInTrace);
                if (action.IncludeInTrace)
                {
                    _currentAction.Subtraces.Add(action);
                }
            }
            else
            {
                _trace.Action = action;
                action.TraceAddress = Array.Empty<int>();
            }

            _actionStack.Push(action);
            _currentAction = action;

            if (IsTracingInstructions)
            {
                (ParityVmTrace VmTrace, List<ParityVmOperationTrace> Ops) currentVmTrace = (new ParityVmTrace(), new List<ParityVmOperationTrace>());
                if (_currentOperation is not null)
                {
                    if (action.Type != "suicide")
                    {
                        _currentOperation.Sub = currentVmTrace.VmTrace;
                    }
                }

                _vmTraceStack.Push(currentVmTrace);
                _currentVmTrace = currentVmTrace;
                _trace.VmTrace ??= _currentVmTrace.VmTrace;
            }
        }

        private void PopAction()
        {
            if (IsTracingInstructions)
            {
                _currentVmTrace.VmTrace.Operations = _currentVmTrace.Ops.ToArray();
                _vmTraceStack.Pop();
                _currentVmTrace = _vmTraceStack.Count == 0 ? (null, null) : _vmTraceStack.Peek();
                _currentOperation = _currentVmTrace.Ops?.Last();
                _gasAlreadySetForCurrentOp = false;

                if (_actionStack.Peek().Type != "suicide")
                {
                    _treatGasParityStyle = true;
                }
            }

            _actionStack.Pop();
            _currentAction = _actionStack.Count == 0 ? null : _actionStack.Peek();
        }

        public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            if (_currentAction is not null)
            {
                throw new InvalidOperationException($"Closing trace at level {_currentAction.TraceAddress?.Length ?? 0}");
            }

            if (_trace.Action!.TraceAddress!.Length == 0)
            {
                _trace.Output = output;
            }

            _trace.Action!.Result!.Output = output;
        }

        public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        {
            if (_currentAction is not null)
            {
                throw new InvalidOperationException($"Closing trace at level {_currentAction!.TraceAddress!.Length}");
            }

            _trace.Output = output;

            // quick tx fail (before execution)
            if (_trace.Action is null)
            {
                _trace.Action = new ParityTraceAction
                {
                    From = _tx!.SenderAddress,
                    To = _tx.To,
                    Value = _tx.Value,
                    Input = _tx.Data.AsArray(),
                    Gas = _tx.GasLimit,
                    CallType = _tx.IsMessageCall ? "call" : "init",
                    Error = error
                };
            }
        }

        public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
        {
            ParityVmOperationTrace operationTrace = new();
            _gasAlreadySetForCurrentOp = false;
            operationTrace.Pc = pc;
            operationTrace.Cost = gas;
            _currentOperation = operationTrace;
            _currentPushList.Clear();
            _currentVmTrace.Ops.Add(operationTrace);
        }

        public override void ReportOperationError(EvmExceptionType error)
        {
            if (error != EvmExceptionType.InvalidJumpDestination &&
                error != EvmExceptionType.NotEnoughBalance)
            {
                _currentVmTrace.Ops.Remove(_currentOperation);
            }
        }

        public override void ReportOperationRemainingGas(long gas)
        {
            if (!_gasAlreadySetForCurrentOp)
            {
                _gasAlreadySetForCurrentOp = true;

                _currentOperation!.Cost -= (_treatGasParityStyle ? 0 : gas);

                // based on Parity behaviour - adding stipend to the gas cost
                if (_currentOperation.Cost == 7400)
                {
                    _currentOperation.Cost = 9700;
                }

                _currentOperation.Push = _currentPushList.ToArray();
                _currentOperation.Used = gas;

                _treatGasParityStyle = false;
            }
        }

        public override void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
            _currentPushList.Add(stackItem.ToArray());
        }

        public override void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
            if (data.Length != 0)
            {
                _currentOperation!.Memory = new ParityMemoryChangeTrace { Offset = offset, Data = data.ToArray() };
            }
        }

        public override void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
            _currentOperation!.Store = new ParityStorageChangeTrace { Key = key.ToArray(), Value = value.ToArray() };
        }

        public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            if (_trace.StateChanges is null)
            {
                throw new InvalidOperationException($"{nameof(ParityLikeTxTracer)} did not expect state change report.");
            }

            if (!_trace.StateChanges.ContainsKey(address))
            {
                _trace.StateChanges[address] = new ParityAccountStateChange();
            }
            else
            {
                before = _trace.StateChanges[address].Balance?.Before ?? before;
            }

            _trace.StateChanges[address].Balance = new ParityStateChange<UInt256?>(before, after);
        }

        public override void ReportCodeChange(Address address, byte[] before, byte[] after)
        {
            if (_trace.StateChanges is null)
            {
                throw new InvalidOperationException($"{nameof(ParityLikeTxTracer)} did not expect state change report.");
            }

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

        public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            if (!_trace.StateChanges!.ContainsKey(address))
            {
                _trace.StateChanges[address] = new ParityAccountStateChange();
            }
            else
            {
                before = _trace.StateChanges[address].Nonce?.Before ?? before;
            }

            _trace.StateChanges[address].Nonce = new ParityStateChange<UInt256?>(before, after);
        }

        public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            if (!_trace.StateChanges!.ContainsKey(storageCell.Address))
            {
                _trace.StateChanges[storageCell.Address] = new ParityAccountStateChange();
            }

            Dictionary<UInt256, ParityStateChange<byte[]>> storage =
                _trace.StateChanges[storageCell.Address].Storage ?? (_trace.StateChanges[storageCell.Address].Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>());

            if (storage.TryGetValue(storageCell.Index, out ParityStateChange<byte[]> value))
            {
                before = value.Before ?? before;
            }

            storage[storageCell.Index] = new ParityStateChange<byte[]>(before, after);
        }

        public override void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            ParityTraceAction action = new()
            {
                IsPrecompiled = isPrecompileCall,
                // ignore pre compile calls with Zero value that originates from contracts
                IncludeInTrace = !(isPrecompileCall && callType != ExecutionType.Transaction && value.IsZero),
                From = from,
                To = to,
                Value = value,
                Input = input.ToArray(),
                Gas = gas,
                CallType = GetCallType(callType),
                Type = GetActionType(callType),
                CreationMethod = GetCreateMethod(callType)
            };

            if (_currentOperation is not null && callType.IsAnyCreate())
            {
                // another Parity quirkiness
                _currentOperation.Cost += gas;
            }

            PushAction(action);
        }

        private string? GetCreateMethod(ExecutionType callType)
        {
            switch (callType)
            {
                case ExecutionType.Create:
                    return "create";
                case ExecutionType.Create2:
                    return "create2";
                default:
                    return null;
            }
        }

        public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            ParityTraceAction action = new() { From = address, To = refundAddress, Value = balance, Type = "suicide" };
            PushAction(action);
            _currentAction!.Result = null;
            PopAction();
        }

        public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            if (_currentAction!.Result is null)
            {
                throw new InvalidOperationException(
                    $"{nameof(ReportActionEnd)} called when result is not yet prepared.");
            }

            _currentAction.Result.Output = output.ToArray();
            _currentAction.Result.GasUsed = _currentAction.Gas - gas;
            PopAction();
        }

        public override void ReportActionError(EvmExceptionType evmExceptionType)
        {
            _currentAction!.Result = null;
            _currentAction.Error = GetErrorDescription(evmExceptionType);
            PopAction();
        }

        public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            if (_currentAction!.Result is null)
            {
                throw new InvalidOperationException(
                    $"{nameof(ReportActionEnd)} called when result is not yet prepared.");
            }

            _currentAction.Result.Address = deploymentAddress;
            _currentAction.Result.Code = deployedCode.ToArray();
            _currentAction.Result.GasUsed = _currentAction.Gas - gas;
            PopAction();
        }

        public override void ReportByteCode(byte[] byteCode)
        {
            _currentVmTrace.VmTrace.Code = byteCode;
        }

        public override void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
            _currentOperation!.Used = gasAvailable;
        }
    }
}
