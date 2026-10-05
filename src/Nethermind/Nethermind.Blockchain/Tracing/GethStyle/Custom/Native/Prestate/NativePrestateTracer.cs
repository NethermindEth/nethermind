// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;

public class NativePrestateTracer : GethLikeNativeTxTracer, IInstructionTracingFilter
{
    public const string PrestateTracer = "prestateTracer";

    public UInt256 InstructionMask => CaptureMask;

    private static readonly UInt256 CaptureMask = CreateCaptureMask();
    private static readonly EthereumEcdsa AuthorityRecovery = new(0);

    private readonly IWorldState? _worldState;
    private readonly Hash256? _txHash;
    private TraceMemory _memoryTrace;
    private Instruction _op;
    private Address? _executingAccount;
    private EvmExceptionType? _error;
    private readonly Dictionary<AddressAsKey, NativePrestateTracerAccount> _prestate = [];
    private readonly Dictionary<AddressAsKey, NativePrestateTracerAccount> _poststate;
    private readonly HashSet<AddressAsKey> _createdAccounts;
    private readonly HashSet<AddressAsKey> _deletedAccounts;
    private readonly bool _isEip6780Enabled;
    private readonly bool _isEip7702Enabled;
    private readonly bool _diffMode;
    private readonly bool _includeEmpty;
    private readonly bool _isFrameTx;
    private readonly bool _disableCode;
    private readonly bool _disableStorage;

    public NativePrestateTracer(
        IWorldState worldState,
        GethTraceOptions options,
        Hash256? txHash,
        Address? from,
        Address? to = null,
        Address? beneficiary = null,
        Transaction? transaction = null)
        : this(worldState, options, txHash, from, to, beneficiary, null, transaction?.AuthorizationList, transaction)
    {
    }

    internal NativePrestateTracer(
        IWorldState worldState,
        GethTraceOptions options,
        Hash256? txHash,
        Address? from,
        Address? to,
        Address? beneficiary,
        IReleaseSpec? spec,
        AuthorizationTuple[]? authorizations = null,
        Transaction? transaction = null)
        : base(options)
    {
        IsTracingActions = true;
        IsTracingMemory = true;
        IsTracingStack = true;
        IsTracingOpLevelStorage = false;
        IsTracingReturnData = false;

        _worldState = worldState;
        _isFrameTx = transaction?.Frames is not null;
        _txHash = txHash;
        _isEip6780Enabled = spec?.IsEip6780Enabled ?? false;
        _isEip7702Enabled = spec?.IsEip7702Enabled ?? false;

        NativePrestateTracerConfig config = TypeInfoJsonSerializer.Deserialize<NativePrestateTracerConfig>(options.TracerConfig, EthereumJsonSerializer.JsonOptions) ?? new NativePrestateTracerConfig();
        _diffMode = config.DiffMode;
        _includeEmpty = config.IncludeEmpty;
        if (_diffMode && _includeEmpty)
            throw new ArgumentException("cannot use diffMode with includeEmpty");
        _disableCode = config.DisableCode;
        _disableStorage = config.DisableStorage;
        if (_diffMode)
        {
            _poststate = [];
            _deletedAccounts = [];
            _createdAccounts = [];
        }

        LookupAccount(from!);
        if (transaction?.Frames is null)
        {
            Address recipient = to ?? ContractAddress.From(from, _prestate[from].Nonce ?? 0);
            LookupAccount(recipient);
            if (to is not null) LookupDelegation(to);
            if (_diffMode && to is null)
                _createdAccounts.Add(recipient);
        }
        else
            LookupFrameTxState(from, transaction);
        LookupAccount(beneficiary ?? Address.Zero);
        if (authorizations is not null)
        {
            // Geth captures recoverable authorities even when chain ID or nonce prevents applying the authorization.
            foreach (AuthorizationTuple authorization in authorizations)
            {
                if (HasValidAuthoritySignature(authorization.AuthoritySignature)
                    && (authorization.Authority ??= AuthorityRecovery.RecoverAddress(authorization)) is { } authority)
                    LookupAccount(authority);
            }
        }
    }

    private static bool HasValidAuthoritySignature(Signature signature)
    {
        UInt256 r = new(signature.RAsSpan, isBigEndian: true);
        UInt256 s = new(signature.SAsSpan, isBigEndian: true);
        return (signature.V == Signature.VOffset || signature.V == Signature.VOffset + 1)
            && !r.IsZero && r < SecP256k1Curve.N && !s.IsZero && s <= SecP256k1Curve.HalfN;
    }

    /// <summary>Records the state an EIP-8141 transaction touches outside the VM, before anything reports it.</summary>
    /// <remarks>A frame transaction creates no contract. The payer, always a frame target, is charged at approval,
    /// which default code performs without entering the VM; approval also consumes EIP-8250 keyed nonces through
    /// <c>NONCE_MANAGER</c> storage, and EIP-8272 references are checked against <c>RECENT_ROOT</c> storage before
    /// the first frame, so every frame target, consumed nonce slot and referenced root cell is read up front.</remarks>
    private void LookupFrameTxState(Address sender, Transaction transaction)
    {
        foreach (TxFrame frame in transaction.Frames!)
            LookupAccount(frame.Target ?? sender);

        if (transaction.NonceKeys is { } nonceKeys && KeyedNonceManager.UsesKeyedDomain(nonceKeys))
        {
            foreach (UInt256 nonceKey in nonceKeys)
                LookupStorage(KeyedNonceManager.StorageSlot(sender, nonceKey));
        }

        if (transaction.RecentRootReferences is { } references)
        {
            foreach (RecentRootReference reference in references)
                LookupStorage(RecentRootStore.ReferenceCell(reference.SourceId, reference.Slot));
        }
    }

    private void LookupStorage(in StorageCell cell)
    {
        LookupAccount(cell.Address);
        LookupStorage(cell.Address, cell.Index);
    }

    protected override GethLikeTxTrace CreateTrace() => new();

    private static UInt256 CreateCaptureMask()
    {
        UInt256 mask = UInt256.Zero;
        for (int opcode = 0; opcode <= byte.MaxValue; opcode++)
        {
            if (RequiresStack((Instruction)opcode))
                mask |= UInt256.One << opcode;
        }
        return mask;
    }

    private static bool RequiresStack(Instruction opcode) => opcode is Instruction.SLOAD or Instruction.SSTORE
        or Instruction.EXTCODECOPY or Instruction.EXTCODEHASH or Instruction.EXTCODESIZE
        or Instruction.BALANCE or Instruction.SELFDESTRUCT
        or Instruction.DELEGATECALL or Instruction.CALL or Instruction.STATICCALL or Instruction.CALLCODE
        or Instruction.CREATE or Instruction.CREATE2;

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace result = base.BuildResult();

        // Frame replay needs its entry point and storage-only protocol accounts even when their account fields are empty.
        if (!_includeEmpty && !_isFrameTx)
        {
            foreach ((AddressAsKey address, NativePrestateTracerAccount account) in _prestate)
            {
                if (account.Balance.GetValueOrDefault().IsZero && account.Nonce.GetValueOrDefault().IsZero && account.CodeHash is null)
                    _prestate.Remove(address);
            }
        }

        result.TxHash = _txHash;
        result.CustomTracerResult = new GethLikeCustomTrace
        {
            Value = _diffMode
                ? new NativePrestateTracerDiffMode { pre = _prestate, post = _poststate }
                : _prestate
        };

        return result;
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        if (_diffMode)
            ProcessDiffState();
    }

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        base.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        if (_diffMode)
            ProcessDiffState();
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        base.StartOperation(pc, opcode, gas, env);

        _error = null;

        _op = opcode;
        _executingAccount = env.ExecutingAccount;

        IsTracingMemory = _op == Instruction.CREATE2;
        IsTracingStack = RequiresStack(_op);
    }

    public override void SetOperationMemory(TraceMemory memoryTrace)
    {
        base.SetOperationMemory(memoryTrace);
        _memoryTrace = memoryTrace;
    }

    public override void SetOperationStack(TraceStack stack)
    {
        base.SetOperationStack(stack);

        // A wrapping tracer may keep asking for the stack after the flags were cleared above.
        if (_error is not null) return;

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
                    if (_diffMode && _op == Instruction.SELFDESTRUCT &&
                        (!_isEip6780Enabled || _createdAccounts.Contains(_executingAccount!)))
                        _deletedAccounts.Add(_executingAccount!);
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
                    LookupDelegation(address);
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
                        // The VM reports invalid CREATE2 memory ranges.
                    }
                }
                break;
            case Instruction.CREATE:
                ulong nonce = _worldState!.GetNonce(_executingAccount!);
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

    private void LookupDelegation(Address address)
    {
        if (_isEip7702Enabled && ICodeInfoRepository.TryGetDelegatedAddress(_worldState!.GetCode(address).Span, out Address? target))
            LookupAccount(target);
    }

    protected void LookupAccount(Address addr)
    {
        if (!_prestate.ContainsKey(addr))
        {
            if (_worldState!.TryGetAccount(addr, out AccountStruct account))
            {
                UInt256 nonce = account.Nonce;
                ReadOnlyMemory<byte> code = _disableCode ? default : _worldState.GetCode(addr);
                _prestate.Add(addr, new NativePrestateTracerAccount(account.Balance, nonce, code)
                {
                    CodeHash = account.CodeHash != default(ValueHash256) && account.CodeHash != Keccak.OfAnEmptyString.ValueHash256
                        ? account.CodeHash : (ValueHash256?)null
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

    protected void LookupStorage(Address addr, UInt256 index)
    {
        if (_disableStorage) return;

        NativePrestateTracerAccount account = _prestate[addr];
        account.Storage ??= [];

        if (!account.Storage.ContainsKey(index))
        {
            _worldState!.Get(new StorageCell(addr, index), out UInt256 storage);
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
            NativePrestateTracerAccount poststateAccount = new(
                poststateAccountStruct.Balance,
                poststateAccountStruct.Nonce,
                _disableCode ? default : _worldState.GetCode(addr));
            NativePrestateTracerAccount? diffAccount = new();

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
            ValueHash256 postCodeHash = _worldState.AccountExists(addr) ? poststateAccountStruct.CodeHash : default;
            if (postCodeHash != (prestateAccount.CodeHash ?? Keccak.OfAnEmptyString.ValueHash256))
            {
                modified = true;
                diffAccount.CodeHash = postCodeHash;
            }
            if (!_disableCode && !poststateAccount.Code.Span.SequenceEqual(prestateAccount.Code.Span))
            {
                modified = true;
                diffAccount.Code = poststateAccount.Code;
                diffAccount.IncludeEmptyCode = poststateAccount.Code.IsEmpty;
            }

            if (!_disableStorage && prestateAccount.Storage is not null)
            {
                foreach ((UInt256 index, UInt256 prestateStorage) in prestateAccount.Storage)
                {
                    // Remove any empty slots from the state diff
                    if (prestateStorage.IsZero)
                        prestateAccount.Storage.Remove(index);

                    _worldState!.Get(new StorageCell(addr, index), out UInt256 poststateStorage);
                    if (!prestateStorage.Equals(poststateStorage))
                    {
                        modified = true;
                        if (!poststateStorage.IsZero)
                        {
                            diffAccount.Storage ??= [];
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

            if (!modified)
                _prestate.Remove(addr);
        }
    }
}
