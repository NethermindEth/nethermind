// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

public sealed class NativePrestateTracer : GethLikeNativeTxTracer
{
    public const string PrestateTracer = "prestateTracer";

    private TraceMemory _memoryTrace;
    private Instruction _op;
    private Address? _executingAccount;
    private EvmExceptionType? _error;
    private readonly Dictionary<AddressAsKey, NativePrestateTracerAccount> _prestate;

    public NativePrestateTracer(
        IWorldState worldState,
        NativeTracerContext context,
        GethTraceOptions options) : base(worldState, options)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
        IsTracingStack = true;

        _prestate = new Dictionary<AddressAsKey, NativePrestateTracerAccount>();
        LookupInitialTransactionAccounts(context);
    }

    protected override GethLikeTxTrace CreateTrace() => new();

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        result.CustomTracerResult = new GethLikeCustomTrace() { Value = _prestate };
        return result;
    }

    public override void StartOperation(int depth, long gas, Instruction opcode, int pc, Address executingAccount, bool isPostMerge = false)
    {
        base.StartOperation(depth, gas, opcode, pc, executingAccount, isPostMerge);
        _op = opcode;
        _executingAccount = executingAccount;
    }

    public override void SetOperationMemory(TraceMemory memoryTrace)
    {
        base.SetOperationMemory(memoryTrace);
        _memoryTrace = memoryTrace;
    }

    public override void SetOperationStack(TraceStack stack)
    {
        base.SetOperationStack(stack);

        if (_error is not null)
        {
            return;
        }

        int stackLen = stack.Count;
        Address address;

        switch (_op)
        {
            case Instruction.SLOAD:
            case Instruction.SSTORE:
                if (stackLen >= 1)
                {
                    UInt256 index = stack.PeekUInt256(0);
                    LookupStorage(_executingAccount!, index);
                }
                break;
            case Instruction.EXTCODECOPY:
            case Instruction.EXTCODEHASH:
            case Instruction.EXTCODESIZE:
            case Instruction.BALANCE:
            case Instruction.SELFDESTRUCT:
                if (stackLen >= 1)
                {
                    address = stack.PeekAddress(0);
                    LookupAccount(address);
                }
                break;
            case Instruction.DELEGATECALL:
            case Instruction.CALL:
            case Instruction.STATICCALL:
            case Instruction.CALLCODE:
                if (stackLen >= 5)
                {
                    address = stack.PeekAddress(1);
                    LookupAccount(address);
                }
                break;
            case Instruction.CREATE2:
                if (stackLen >= 4)
                {
                    int offset = stack.Peek(1).ReadEthInt32();
                    int length = stack.Peek(2).ReadEthInt32();
                    ReadOnlySpan<byte> initCode = _memoryTrace.Slice(offset, length);
                    ReadOnlySpan<byte> salt = stack.Peek(3);
                    address = ContractAddress.From(_executingAccount!, salt, initCode);
                    LookupAccount(address);
                    _executingAccount = address;
                }
                break;
            case Instruction.CREATE:
                UInt256 nonce = _worldState!.GetNonce(_executingAccount!);
                address = ContractAddress.From(_executingAccount, nonce);
                LookupAccount(address!);
                _executingAccount = address;
                break;
        }
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        base.ReportOperationError(error);
        _error = error;
    }

    private void LookupInitialTransactionAccounts(NativeTracerContext context)
    {
        Address from = context.From!;
        LookupAccount(from);

        Address to = context.To;
        if (to is null)
        {
            UInt256 fromNonce = _prestate[from].Nonce ?? 0;
            to = ContractAddress.From(from, fromNonce);
        }
        LookupAccount(to);

        // Look up client beneficiary address as well
        LookupAccount(context.Beneficiary ?? Address.Zero);
    }

    private void LookupAccount(Address addr)
    {
        if (!_prestate.ContainsKey(addr))
        {
            if (_worldState!.TryGetAccount(addr, out AccountStruct account))
            {
                ulong nonce = (ulong)account.Nonce;
                byte[]? code = _worldState.GetCode(addr);
                _prestate.Add(addr, new NativePrestateTracerAccount
                {
                    Balance = account.Balance,
                    Nonce = nonce > 0 ? nonce : null,
                    Code = code is not null && code.Length > 0 ? code : null
                });
            }
            else
            {
                _prestate.Add(addr, new NativePrestateTracerAccount
                {
                    Balance = UInt256.Zero
                });
            }
        }
    }

    private void LookupStorage(Address addr, UInt256 index)
    {
        _prestate[addr].Storage ??= new Dictionary<UInt256, UInt256>();

        if (!_prestate[addr].Storage.ContainsKey(index))
        {
            UInt256 storage = new(_worldState!.Get(new StorageCell(addr, index)), true);
            _prestate[addr].Storage.Add(index, storage);
        }
    }
}
