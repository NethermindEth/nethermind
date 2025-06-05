// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.State;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

public sealed class NativePrestateTracer : GethLikeNativeTxTracer
{
    public const string PrestateTracer = "prestateTracer";

    private readonly IWorldState? _worldState;
    private readonly Hash256? _txHash;
    private TraceMemory _memoryTrace;
    private Instruction _op;
    private Address? _executingAccount;
    private EvmExceptionType? _error;
    private readonly Dictionary<AddressAsKey, NativePrestateTracerAccount> _prestate = new();
    private readonly Dictionary<AddressAsKey, NativePrestateTracerAccount> _poststate;
    private readonly HashSet<AddressAsKey> _createdAccounts;
    private readonly HashSet<AddressAsKey> _deletedAccounts;
    private readonly bool _diffMode;

    public NativePrestateTracer(
        IWorldState worldState,
        GethTraceOptions options,
        Hash256? txHash,
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
        _txHash = txHash;

        NativePrestateTracerConfig config = options.TracerConfig?.Deserialize<NativePrestateTracerConfig>(EthereumJsonSerializer.JsonOptions) ?? new NativePrestateTracerConfig();
        _diffMode = config.DiffMode;
        if (_diffMode)
        {
            _poststate = new Dictionary<AddressAsKey, NativePrestateTracerAccount>();
            _deletedAccounts = new HashSet<AddressAsKey>();
            _createdAccounts = new HashSet<AddressAsKey>();
        }

        LookupAccount(from!);
        LookupAccount(to ?? ContractAddress.From(from, _prestate[from].Nonce ?? 0));
        LookupAccount(beneficiary ?? Address.Zero);
    }

    protected override GethLikeTxTrace CreateTrace() => new();

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        result.TxHash = _txHash;
        result.CustomTracerResult = new GethLikeCustomTrace
        {
            Value = _diffMode
                ? new NativePrestateTracerDiffMode { pre = _prestate, post = _poststate }
                : _prestate
        };

        return result;
    }

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        if (_diffMode)
            ProcessDiffState();
    }

    public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        if (_diffMode)
            ProcessDiffState();
    }

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
    {
        base.StartOperation(pc, opcode, gas, env, codeSection, functionDepth);

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
                    if (_diffMode && _op == Instruction.SELFDESTRUCT)
                        _deletedAccounts.Add(address);
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
                        if (_diffMode)
                            _createdAccounts.Add(address);
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
                if (_diffMode)
                    _createdAccounts.Add(address);
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
                UInt256 nonce = account.Nonce;
                byte[]? code = _worldState.GetCode(addr);
                _prestate.Add(addr, new NativePrestateTracerAccount(account.Balance, nonce, code));
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
        NativePrestateTracerAccount account = _prestate[addr];
        account.Storage ??= new Dictionary<UInt256, UInt256>();

        if (!account.Storage.ContainsKey(index))
        {
            UInt256 storage = new(_worldState!.Get(new StorageCell(addr, index)), true);
            account.Storage.Add(index, storage);
        }
    }

    private void ProcessDiffState()
    {
        foreach ((AddressAsKey addr, NativePrestateTracerAccount prestateAccount) in _prestate)
        {
            // If an account was deleted then don't show it in the postState trace
            if (_deletedAccounts.Contains(addr))
                continue;

            _worldState!.TryGetAccount(addr, out AccountStruct poststateAccountStruct);
            NativePrestateTracerAccount poststateAccount = new NativePrestateTracerAccount(
                poststateAccountStruct.Balance,
                poststateAccountStruct.Nonce,
                _worldState.GetCode(addr));
            NativePrestateTracerAccount? diffAccount = new NativePrestateTracerAccount();

            bool modified = false;
            if (!poststateAccount.Balance.Equals(prestateAccount.Balance))
            {
                modified = true;
                diffAccount.Balance = poststateAccount.Balance;
            }
            if (!poststateAccount.Nonce.Equals(prestateAccount.Nonce))
            {
                modified = true;
                diffAccount.Nonce = poststateAccount.Nonce;
            }
            if (!Bytes.NullableEqualityComparer.Equals(poststateAccount.Code, prestateAccount.Code))
            {
                modified = true;
                diffAccount.Code = poststateAccount.Code;
            }

            if (prestateAccount.Storage is not null)
            {
                foreach ((UInt256 index, UInt256 prestateStorage) in prestateAccount.Storage)
                {
                    // Remove any empty slots from the state diff
                    if (prestateStorage.IsZero)
                        prestateAccount.Storage.Remove(index);

                    UInt256 poststateStorage = new(_worldState!.Get(new StorageCell(addr, index)), true);
                    if (!prestateStorage.Equals(poststateStorage))
                    {
                        modified = true;
                        if (!poststateStorage.IsZero)
                        {
                            diffAccount.Storage ??= new Dictionary<UInt256, UInt256>();
                            diffAccount.Storage.Add(index, poststateStorage);
                        }
                    }
                    else
                    {
                        // Remove the storage slot from the prestate trace if it wasn't modified
                        prestateAccount.Storage.Remove(index);
                    }
                }
            }

            // If any account fields were modified then add the account to the poststate trace
            if (modified)
                _poststate.Add(addr, diffAccount);

            // If no account fields were modified or the account was created then remove it from the prestate trace
            if (!modified || _createdAccounts.Contains(addr))
                _prestate.Remove(addr);
        }
    }
}
