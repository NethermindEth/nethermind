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
    // private readonly UInt256 _gasPrice;
    // private readonly UInt256 _gasLimit;
    private readonly Address _contractAddress;
    private TraceMemory _memoryTrace;
    private Instruction _op;
    private readonly Dictionary<Address, NativePrestateTracerAccount> _prestate;
    private Address? FromAddress { get; set; }

    public NativePrestateTracer(
        IWorldState worldState,
        GethLikeBlockNativeTracer.Context context,
        GethTraceOptions options) : base(worldState, options)
    {
        IsTracingRefunds = true;
        IsTracingActions = true;
        IsTracingMemory = true;
        IsTracingStack = true;

        _prestate = new Dictionary<Address, NativePrestateTracerAccount>();
        // _gasPrice = context.GasPrice;
        // _gasLimit = new UInt256(context.GasLimit.ToBigEndianByteArray(), true);
        _contractAddress = context.ContractAddress;
    }

    protected override GethLikeTxTrace CreateTrace() => new();

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        result.CustomTracerResult = new GethLikeCustomTrace() { Value = _prestate };
        return result;
    }

    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);

        if (Depth == 0)
        {
            //TODO: investigate incorrect balance for from address
            FromAddress = from;
            CaptureStart(value, from, to);
        }
    }

    // public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    // {
    //     base.ReportActionEnd(gas, deploymentAddress, deployedCode);
    //
    //     if (Depth == 0)
    //     {
    //         //TODO: investigate incorrect balance for from address
    //     }
    // }
    //
    // public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    // {
    //     base.ReportActionEnd(gas, output);
    //
    //     if (Depth == 0)
    //     {
    //         //TODO: investigate incorrect balance for from address
    //     }
    // }

    public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
    {
        base.StartOperation(depth, gas, opcode, pc, isPostMerge);

        _op = opcode;
    }

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        base.SetOperationStorage(address, storageIndex, newValue, currentValue);

        LookupStorage(address, storageIndex);
    }

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        base.LoadOperationStorage(address, storageIndex, value);

        LookupStorage(address, storageIndex);
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
            case Instruction.EXTCODECOPY:
            case Instruction.EXTCODEHASH:
            case Instruction.EXTCODESIZE:
            case Instruction.BALANCE:
            case Instruction.SELFDESTRUCT:
                if (stackLen >= 1)
                {
                    address = stack.Peek(0).ToHexString(true).ToAddress();
                    LookupAccount(address);
                }
                break;
            case Instruction.DELEGATECALL:
            case Instruction.CALL:
            case Instruction.STATICCALL:
            case Instruction.CALLCODE:
                if (stackLen >= 5)
                {
                    address = stack.Peek(1).ToHexString(true).ToAddress();
                    LookupAccount(address);
                }
                break;
            case Instruction.CREATE2:
                if (stackLen >= 4)
                {
                    int offset = stack.Peek(1).ToInt32(null);
                    int length = stack.Peek(2).ToInt32(null);
                    ReadOnlySpan<byte> initCode = _memoryTrace.Slice(offset, length);
                    string salt = stack.Peek(3).ToHexString(true);
                    address = ContractAddress.From(_contractAddress, Bytes.FromHexString(salt, EvmStack.WordSize), initCode);
                    LookupAccount(address);
                }
                break;
            case Instruction.CREATE:
                LookupAccount(_contractAddress);
                break;
        }
    }

    private void CaptureStart(UInt256 value, Address from, Address to)
    {
        LookupAccount(from);
        LookupAccount(to);

        // Look up client coinbase address as well
        LookupAccount(Address.Zero);

        _prestate[from].Nonce -= 1;
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
            _prestate[addr].Storage.Add(key, storage.PadLeft(32).ToHexString(true));
        }
    }
}
