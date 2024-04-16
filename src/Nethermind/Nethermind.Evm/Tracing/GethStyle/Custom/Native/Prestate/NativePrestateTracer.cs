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

    private readonly IWorldState? _worldState;
    private TraceMemory _memoryTrace;
    private Instruction _op;
    private Address? _executingAccount;
    private EvmExceptionType? _error;
    private readonly Dictionary<AddressAsKey, NativePrestateTracerAccount> _prestate = new();

    public NativePrestateTracer(IWorldState worldState,
        GethTraceOptions options,
        Address? from,
        Address? to = null,
        Address? beneficiary = null)
        : base(options)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
        IsTracingStack = true;

        _worldState = worldState;

        LookupAccount(from);
        LookupAccount(to ?? ContractAddress.From(from, _prestate[from].Nonce ?? 0));
        LookupAccount(beneficiary ?? Address.Zero);
    }

    protected override GethLikeTxTrace CreateTrace() => new();

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        result.CustomTracerResult = new GethLikeCustomTrace() { Value = _prestate };
        return result;
    }

    public override void StartOperation(in ExecutionEnvironment env, long gas, Instruction opcode, int pc)
    {
        base.StartOperation(env, gas, opcode, pc);

        if (_error is not null) return;

        _op = opcode;
        _executingAccount = env.ExecutingAccount;
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
                    try
                    {
                        int offset = stack.Peek(1).ReadEthInt32();
                        int length = stack.Peek(2).ReadEthInt32();
                        ReadOnlySpan<byte> initCode = _memoryTrace.Slice(offset, length);
                        ReadOnlySpan<byte> salt = stack.Peek(3);
                        address = ContractAddress.From(_executingAccount!, salt, initCode);
                        LookupAccount(address);
                    }
                    catch
                    {
                        /*
                         * This operation error will be recorded in ReportOperationError and all
                         * subsequent operations will be ignored from the prestate trace
                         */
                    }
                }
                break;
            case Instruction.CREATE:
                UInt256 nonce = _worldState!.GetNonce(_executingAccount!);
                address = ContractAddress.From(_executingAccount, nonce);
                LookupAccount(address!);
                break;
        }
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        base.ReportOperationError(error);
        _error = error;
    }

    private void LookupAccount(Address addr)
    {
        if (!_prestate.ContainsKey(addr))
        {
            if (_worldState!.TryGetAccount(addr, out AccountStruct account))
            {
                ulong nonce = (ulong)account.Nonce;
                byte[]? code = _worldState.GetCode(addr);
                _prestate.Add(addr, new NativePrestateTracerAccount(account.Balance, nonce, code));
            }
            else
            {
                _prestate.Add(addr, new NativePrestateTracerAccount(UInt256.Zero));
            }
        }
    }

    private void LookupStorage(Address addr, UInt256 index)
    {
        NativePrestateTracerAccount account = _prestate[addr];
        account.Storage ??= new Dictionary<UInt256, UInt256>();

        if (!account.Storage.ContainsKey(index))
        {
            UInt256 storage = new(_worldState!.Get(new StorageCell(addr, index)), true);
            account.Storage.Add(index, storage);
        }
    }
}
