// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

public sealed class NativePrestateTracer : GethLikeNativeTxTracer
{
    public const string PrestateTracer = "prestateTracer";

    private TraceMemory _memoryTrace;
    private Instruction _op;
    private Address? _executingAccount;
    private readonly Dictionary<Address, NativePrestateTracerAccount> _prestate;

    public NativePrestateTracer(
        IWorldState worldState,
        NativeTracerContext context,
        GethTraceOptions options) : base(worldState, options)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
        IsTracingStack = true;

        _prestate = new Dictionary<Address, NativePrestateTracerAccount>();

        if (context.From is not null)
        {
            LookupAccount(context.From);
        }
        if (context.To is not null)
        {
            LookupAccount(context.To);
        }

        // Look up client coinbase address as well
        LookupAccount(Address.Zero);
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

        int stackLen = stack.Count;
        Address address;

        switch (_op)
        {
            case Instruction.SLOAD:
            case Instruction.SSTORE:
                if (stackLen > 1)
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
                    address = stack.Peek(0).ToHexString(true, true).ToAddress();
                    LookupAccount(address);
                }
                break;
            case Instruction.DELEGATECALL:
            case Instruction.CALL:
            case Instruction.STATICCALL:
            case Instruction.CALLCODE:
                if (stackLen >= 5)
                {
                    address = stack.Peek(1).ToHexString(true, true).ToAddress();
                    LookupAccount(address);
                }
                break;
            case Instruction.CREATE2:
                if (stackLen >= 4)
                {
                    int offset = int.Parse(stack.Peek(1));
                    int length = int.Parse(stack.Peek(2));
                    ReadOnlySpan<byte> initCode = _memoryTrace.Slice(offset, length);
                    ReadOnlySpan<byte> salt = stack.Peek(3);
                    address = ContractAddress.From(_executingAccount!, salt, initCode);
                    LookupAccount(address);
                }
                break;
            case Instruction.CREATE:
                LookupAccount(_executingAccount!);
                break;
        }
    }

    private void LookupAccount(Address addr)
    {
        if (!_prestate.ContainsKey(addr) && _worldState!.TryGetAccount(addr, out AccountStruct account))
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
    }

    private void LookupStorage(Address addr, UInt256 index)
    {
        _prestate[addr].Storage ??= new Dictionary<string, string>();

        string key = index.ToHexString(false);
        if (!_prestate[addr].Storage.ContainsKey(key))
        {
            ReadOnlySpan<byte> storage = _worldState!.Get(new StorageCell(addr, index));
            string storageHex = storage.PadLeft(32).ToHexString(true);
            _prestate[addr].Storage.Add(key, storageHex);
        }
    }
}
