// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

/// <summary>Enforces the EIP-8141 validation-prefix opcode rules during prefix simulation, and captures the
/// resolved payer.</summary>
/// <remarks>
/// The storage surface is parameterised because EIP-8141's mempool rule and EIP-8369's Profile 2 draw it
/// differently: EIP-8141 admits every slot of <paramref name="sender"/> alone, Profile 2 admits the first
/// <paramref name="storageSlotBound"/> slots of <paramref name="sender"/> and the payer. Defaults reproduce
/// the EIP-8141 rule exactly.
/// </remarks>
/// <param name="payer">A second account whose storage the prefix may read, or <c>null</c> for
/// <paramref name="sender"/> only.</param>
/// <param name="storageSlotBound">Slots below which a read is in surface, or <c>0</c> for no slot bound.</param>
/// <param name="createInDeployFrame">Whether the deploy frame may install code at tx.sender with <c>CREATE</c> as well as
/// <c>CREATE2</c>, as EIP-8141 and EIP-7805's Profile 2 deployment bookkeeping permit; the public mempool refuses it.</param>
/// <param name="maxCodeBodies">EIP-7805 <c>MAX_REPLAY_CODE_BODIES</c>: the distinct code bodies the prefix may load, or
/// <c>0</c> for no bound. A body is loaded when execution starts in it, after delegation is followed, or when
/// <c>EXTCODESIZE</c> or <c>EXTCODECOPY</c> reads it; empty code loads nothing.</param>
/// <param name="maxCodeBytes">EIP-7805 <c>MAX_REPLAY_CODE_BYTES</c>: their combined size, or <c>0</c> for no bound.</param>
/// <param name="timeout">Wall-clock bound on the simulation, or <see cref="TimeSpan.Zero"/> for none.</param>
/// <param name="timeProvider">The clock <paramref name="timeout"/> is measured against; the system clock by default.</param>
/// <param name="token">Cancels the simulation cooperatively; polled by the interpreter.</param>
/// <param name="preempt">Polled with <paramref name="token"/>; once it returns true the simulation stops and
/// <see cref="Preempted"/> stays set.</param>
/// <param name="maxVerifyGas">The prefix's VERIFY gas bound; EIP-8141's <c>MAX_VERIFY_GAS</c> by default.</param>
public sealed class FrameTxValidationTracer(
    Address sender,
    Address expiryVerifier,
    IReadOnlyStateProvider state,
    IReleaseSpec spec,
    TimeSpan timeout = default,
    TimeProvider? timeProvider = null,
    CancellationToken token = default,
    Func<bool>? preempt = null,
    ulong maxVerifyGas = Eip8141Constants.MaxVerifyGas,
    Address? payer = null,
    UInt256 storageSlotBound = default,
    bool createInDeployFrame = false,
    int maxCodeBodies = 0,
    long maxCodeBytes = 0)
    : TxTracer, ITxTracer, IFrameTxReceiptTracer, IFrameTxPrefixTracer
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly long _deadline = Deadline(timeProvider ?? TimeProvider.System, timeout);

    private static long Deadline(TimeProvider time, TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? time.GetTimestamp() + (long)(timeout.TotalSeconds * time.TimestampFrequency) : 0;

    // Stack slots holding the current CALL*/EXTCODE* target and value operands, or -1. Set in StartOperation
    // and consumed in SetOperationStack, as the operands aren't available any earlier.
    private int _targetStackIndex = -1;
    private int _valueStackIndex = -1;

    /// <summary>Whether the executing frame is the prefix-opening deploy frame, the only one whose
    /// carve-outs let it write state.</summary>
    private bool _inDeployFrame;
    private bool _inExpiryFrame;
    private int _prefixFrameIndex;

    /// <summary>Set between a CREATE2 and the creation frame it is expected to open.</summary>
    private bool _createPending;

    /// <summary>The <c>RECENT_ROOT_ADDRESS</c> storage keys the executing EIP-8272 <c>recent_root_verify</c> frame
    /// derives from its tuples, or <c>null</c> outside one.</summary>
    /// <remarks>EIP-8272 public-mempool rule: the frame is a violation unless the fork is active and that address
    /// runs exactly <c>RECENT_ROOT_CODE</c>, since code needing neither permission would otherwise pass.</remarks>
    private HashSet<UInt256>? _recentRootKeys;

    /// <summary>Whether the current opcode runs <c>RECENT_ROOT_CODE</c> at the top level of a <c>recent_root_verify</c> frame.</summary>
    private bool _inRecentRootCode;

    public override bool IsTracingInstructions => true;
    public override bool IsTracingOpLevelStorage => true;
    public override bool IsTracingStack => true;
    public override bool IsTracingReceipt => true;

    // Explicit: ITxTracer declares these as default members, so the mapping must be made on this type
    // for the interpreter (which polls through the interface) to see them.
    bool ITxTracer.IsCancelable => true;

    /// <inheritdoc/>
    /// <remarks>Polled by the interpreter every 1024 opcodes; aborting at the first violation denies a
    /// spammer the rest of the <c>MAX_VERIFY_GAS</c> budget per rejected transaction.</remarks>
    bool ITxTracer.IsCancelled => Violated || TimedOut || (_preempted = _preempted || preempt?.Invoke() == true) || token.IsCancellationRequested;

    /// <summary>True once the preemption callback asked the simulation to stop.</summary>
    public bool Preempted => _preempted;

    private bool _preempted;

    /// <summary>True once the wall-clock bound was reached; the transaction is then rejected, not cancelled.</summary>
    public bool TimedOut => _deadline != 0 && _time.GetTimestamp() > _deadline;

    // The VM reports the address a creation frame is entered at; recomputing it here would duplicate
    // CREATE2's initcode hashing for no gain.
    public override bool IsTracingActions => true;

    public bool Violated { get; private set; }

    /// <summary>The first violation recorded; later ones do not overwrite it.</summary>
    public string? ViolationReason { get; private set; }

    public Address? Payer { get; private set; }

    public ulong MaxVerifyGas => maxVerifyGas;

    /// <summary>EIP-8298 targets the prefix relied on whose code could change through DELEGATECALL or CALLCODE.</summary>
    public IReadOnlyList<FrameTxCodeDependency> CodeDependencies => _codeDependencies ?? (IReadOnlyList<FrameTxCodeDependency>)[];

    private List<FrameTxCodeDependency>? _codeDependencies;

    void IFrameTxPrefixTracer.StartPrefixFrame(TxFrame frame, bool isDeployFrame, Address target)
    {
        SettleCreate();
        _inDeployFrame = isDeployFrame;
        _recentRootKeys = null;
        if (FrameTxValidation.IsRecentRootVerifyFrame(frame))
        {
            if (!spec.IsEip8272Enabled || state.GetCodeHash(Eip8272Constants.RecentRootAddress) != Eip8272Constants.RecentRootCodeHash)
            {
                Violate("recent_root_verify frame without RECENT_ROOT_CODE at RECENT_ROOT_ADDRESS");
            }
            else
            {
                _recentRootKeys = RecentRootKeys(frame.Data.Span);
            }
        }

        _inExpiryFrame = _prefixFrameIndex++ == 0 && !isDeployFrame && target == expiryVerifier;

        // The processor dispatches this target rather than an opcode, so it never meets the CALL* rule below,
        // and it is the one arbitrary address the prefix executes. A delegated factory is mutable by its
        // authority, which is the dependency that rule exists to close.
        if (isDeployFrame && IsForbiddenCallTarget(target))
        {
            Violate($"deploy frame target {target} is not an undelegated contract");
        }
        else if (isDeployFrame && HasMutableCode(target))
        {
            Violate($"deploy frame target {target} has mutable code");
        }
    }

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        _targetStackIndex = -1;
        _valueStackIndex = -1;
        SettleCreate();
        if (Violated) return;

        _codeReadPending = false;
        // Execution starts in a body at its first instruction. Only a change of executing code can be such a
        // start, so a loop jumping back to offset 0 costs one reference comparison, never a rehash.
        if (_codeBodies is not null && !ReferenceEquals(env.CodeInfo, _executingCode))
        {
            _executingCode = env.CodeInfo;
            if (pc == 0 && _enteredCode!.Add(env.CodeInfo))
            {
                ReadOnlySpan<byte> code = env.CodeInfo.CodeSpan;
                if (!code.IsEmpty) LoadCode(env.CodeInfo.CodeHash != default ? env.CodeInfo.CodeHash : HashCode(code), code.Length);
                if (Violated) return;
            }
        }

        _inRecentRootCode = _recentRootKeys is not null && env.CallDepth == 0 && env.ExecutingAccount == Eip8272Constants.RecentRootAddress;

        switch (opcode)
        {
            case Instruction.GAS:
                // Permitted only immediately before a *CALL, the gas-forwarding idiom that adds no
                // mempool dependency. GAS has no immediate, so the next opcode is the byte at pc + 1.
                ReadOnlySpan<byte> code = env.CodeInfo.CodeSpan;
                int next = pc + 1;
                if (next >= code.Length || !IsCall((Instruction)code[next]))
                {
                    Violate("GAS not immediately followed by a call");
                }
                break;
            case Instruction.TIMESTAMP:
                // EIP-8141 permits TIMESTAMP only in the leading canonical expiry frame.
                if (!_inExpiryFrame || env.CallDepth != 0
                    || state.GetCodeHash(expiryVerifier) != Eip8141Constants.ExpiryVerifierCodeHash)
                {
                    Violate("banned opcode TIMESTAMP in validation prefix");
                }
                break;
            case Instruction.SLOTNUM:
                if (!_inRecentRootCode) Violate($"banned opcode {opcode} in validation prefix");
                break;
            case Instruction.CALL:
            case Instruction.CALLCODE:
                _targetStackIndex = 1; // [gas, address, value, ...]
                _valueStackIndex = 2;
                break;
            case Instruction.DELEGATECALL:
            case Instruction.STATICCALL:
                _targetStackIndex = 1; // [gas, address, ...]
                break;
            case Instruction.EXTCODESIZE:
            case Instruction.EXTCODECOPY:
                _targetStackIndex = 0; // [address, ...]
                _codeReadPending = _codeBodies is not null;
                break;
            case Instruction.EXTCODEHASH:
                _targetStackIndex = 0; // [address, ...]
                break;
            case Instruction.GASPRICE:
            case Instruction.BLOCKHASH:
            case Instruction.COINBASE:
            case Instruction.NUMBER:
            case Instruction.PREVRANDAO:
            case Instruction.GASLIMIT:
            case Instruction.BASEFEE:
            case Instruction.BLOBBASEFEE:
            case Instruction.INVALID:
            case Instruction.SELFDESTRUCT:
            case Instruction.PAY when spec.IsEip5920Enabled:
            case Instruction.BALANCE:
            case Instruction.SELFBALANCE:
                Violate($"banned opcode {opcode} in validation prefix");
                break;
            case Instruction.SETCODEFROM when spec.IsEip8298Enabled:
                // EIP-8298 writes code outside both deploy-frame carve-outs, and copies whatever the source
                // holds at inclusion, which its owner can change with SETCODEFROM.
                Violate($"banned opcode {opcode} in validation prefix");
                break;
            case Instruction.CREATE when !createInDeployFrame || !_inDeployFrame:
                // Its address is f(factory, factory.nonce), which any third party can move by making the
                // factory create again; only CREATE2 makes the deployment a pure function of the transaction.
                // Profile 2 replay judges one fixed state, so it takes the creator's nonce as given.
                Violate($"banned opcode {opcode} in validation prefix");
                break;
            case Instruction.TCREATE when spec.IsEip8360Enabled:
                // EIP-8360: never installs code at tx.sender, the one creation the prefix admits.
                Violate($"banned opcode {opcode} in validation prefix");
                break;
            case Instruction.CREATE:
            case Instruction.CREATE2:
                // Allowed only in the deploy frame, and only to install code at tx.sender; the address is
                // checked when the creation frame opens. Slot 0 is the endowment, banned as a funded CALL is.
                if (!_inDeployFrame)
                {
                    Violate($"banned opcode {opcode} in validation prefix");
                    break;
                }

                _valueStackIndex = 0;
                _createPending = true;
                break;
            case Instruction.SSTORE:
                // Allowed only in the deploy frame; SetOperationStorage confines it to tx.sender.
                if (!_inDeployFrame) Violate($"banned opcode {opcode} in validation prefix");
                break;
        }
    }

    public override void SetOperationStack(TraceStack stack)
    {
        int index = _targetStackIndex;
        int valueIndex = _valueStackIndex;
        _targetStackIndex = -1;
        _valueStackIndex = -1;
        if (Violated) return;

        if (index >= 0)
        {
            // A malformed stack faults the frame and rejects the prefix anyway; skip classification.
            if (stack.Count <= index) return;

            Address target = stack.PeekAddress(index);
            if (_codeReadPending)
            {
                ValueHash256 codeHash = state.GetCodeHash(target);
                if (codeHash != Keccak.OfAnEmptyString.ValueHash256 && !_codeBodies!.Contains(codeHash)) LoadCode(codeHash, state.GetCode(in codeHash).Length);
                if (Violated) return;
            }

            if (IsForbiddenCallTarget(target))
            {
                Violate($"CALL*/EXTCODE* to disallowed target {target} in validation prefix");
                return;
            }

            if (HasMutableCode(target))
            {
                Violate($"CALL*/EXTCODE* target {target} has mutable code in validation prefix");
                return;
            }
        }

        // A funded call or create executes or pushes zero on the caller's balance alone, which is the same
        // one-bit dependency on unindexed state that the BALANCE ban closes; the transfer itself is also a
        // write neither deploy-frame carve-out covers.
        if (valueIndex >= 0 && stack.Count > valueIndex && !stack.PeekUInt256(valueIndex).IsZero)
        {
            Violate(index < 0
                ? "endowed CREATE2 in validation prefix"
                : "value-carrying CALL in validation prefix");
        }
    }

    /// <summary>Distinct code bodies loaded so far, by hash, or <c>null</c> when neither bound applies.</summary>
    private readonly HashSet<ValueHash256>? _codeBodies = maxCodeBodies > 0 || maxCodeBytes > 0 ? [] : null;

    /// <summary>The code instances already counted, by reference: <see cref="CodeInfo"/> overrides equality.</summary>
    private readonly HashSet<CodeInfo>? _enteredCode = maxCodeBodies > 0 || maxCodeBytes > 0 ? new(ReferenceEqualityComparer.Instance) : null;

    private CodeInfo? _executingCode;
    private long _codeBytes;
    private bool _codeReadPending;

    /// <summary>Bodies hashed because their code carried no stamped hash, which only initcode should need.</summary>
    internal int CodeHashComputations { get; private set; }

    private ValueHash256 HashCode(ReadOnlySpan<byte> code)
    {
        CodeHashComputations++;
        return ValueKeccak.Compute(code);
    }

    /// <summary>EIP-7805: counts a code body the prefix loads against <c>MAX_REPLAY_CODE_BODIES</c> and <c>MAX_REPLAY_CODE_BYTES</c>.</summary>
    private void LoadCode(in ValueHash256 codeHash, int length)
    {
        if (!_codeBodies!.Add(codeHash)) return;
        _codeBytes += length;
        if (maxCodeBodies > 0 && _codeBodies.Count > maxCodeBodies) Violate("validation code body count exceeded");
        else if (maxCodeBytes > 0 && _codeBytes > maxCodeBytes) Violate("validation code byte limit exceeded");
    }

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        // SLOAD may read only in-surface storage, including transitively via CALL*/DELEGATECALL.
        if (!Violated && !InSurface(address, in storageIndex) && !IsRecentRootKeyRead(address, storageIndex)) ViolateSurface("SLOAD", address, in storageIndex);
    }

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        // The carve-out is the deploy frame's alone, and covers tx.sender's storage only -- never the payer's,
        // whose slots are readable but not writable. Every other prefix frame is static.
        if (Violated) return;
        if (!_inDeployFrame) Violate("SSTORE outside tx.sender storage");
        else if (address != sender || !InSlotBound(in storageIndex)) ViolateSurface("SSTORE", address, in storageIndex);
    }

    /// <summary>Whether a storage cell the prefix touched is inside the configured surface.</summary>
    private bool InSurface(Address address, in UInt256 slot) =>
        (address == sender || (payer is not null && address == payer)) && InSlotBound(in slot);

    private bool InSlotBound(in UInt256 slot) => storageSlotBound.IsZero || slot < storageSlotBound;

    /// <summary>Records an out-of-surface storage access as a trace-rule violation.</summary>
    // The default surface keeps its original wording; a widened or slot-bounded one names the cell,
    // since the address alone no longer says which half of the rule failed.
    private void ViolateSurface(string op, Address address, in UInt256 slot) =>
        Violate(payer is null && storageSlotBound.IsZero
            ? $"{op} outside tx.sender storage"
            : $"{op} outside the permitted storage surface at {address}:{slot}");

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        if (!callType.IsAnyCreate()) return;

        _createPending = false;
        // The carve-out covers code installed at tx.sender only; a contract deployed anywhere else is a
        // state write whose result the prefix would silently depend on.
        if (!Violated && to != sender) Violate("CREATE outside tx.sender");
    }

    void IFrameTxReceiptTracer.ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts)
    {
        // The prefix ends here, so anything still owed a creation frame has to be settled before the payer
        // stands -- otherwise a trailing CREATE that opened none would be admitted rather than reported.
        SettleCreate();
        Payer = payer;
    }

    /// <summary>Closes out a CREATE2 that opened no creation frame.</summary>
    /// <remarks>Such a create returned zero on a collision, nonce-overflow or call-depth condition, none
    /// of which is an indexed dependency of the transaction, so the prefix must not turn on it.</remarks>
    private void SettleCreate()
    {
        if (!_createPending) return;
        _createPending = false;
        Violate("CREATE opened no creation frame");
    }

    private void Violate(string reason)
    {
        if (Violated) return;
        Violated = true;
        ViolationReason = reason;
    }

    /// <summary>Disallowed unless an undelegated existing contract or a precompile; tx.sender is exempt,
    /// its code hash and nonce being tracked dependencies already.</summary>
    private bool IsForbiddenCallTarget(Address target)
    {
        if (target == sender || spec.IsPrecompile(target)) return false;
        if (!state.IsContract(target)) return true;
        return state.IsDelegatedCode(target);
    }

    /// <summary>Under EIP-8298, whether a contract other than tx.sender can rewrite its own code by running SETCODEFROM.</summary>
    /// <remarks>
    /// Code changes only through SETCODEFROM run in the account's own context, directly or via code it
    /// DELEGATECALLs or CALLCODEs, and EIP-6780 rules out redeployment, so the scan has no false negatives.
    /// Direct SETCODEFROM is refused. A target that can only reach it by delegating, such as a proxy
    /// implementation, is admitted but recorded in <see cref="CodeDependencies"/>, so the pool revalidates
    /// when its code changes and caps how many pending transactions rely on that account.
    /// </remarks>
    private bool HasMutableCode(Address target)
    {
        if (!spec.IsEip8298Enabled || target == sender || spec.IsPrecompile(target)) return false;

        ValueHash256 codeHash = state.GetCodeHash(target);
        (ValueHash256, bool) key = (codeHash, spec.IsEip8024Enabled);
        if (!MutableCodeCache.TryGet(key, out CodeMutability mutability))
        {
            mutability = ScanCodeMutability(state.GetCode(in codeHash).Span, key.Item2);
            MutableCodeCache.Set(key, mutability);
        }

        if (mutability == CodeMutability.ViaDelegation) AddCodeDependency(target, codeHash);
        return mutability == CodeMutability.Direct;
    }

    private void AddCodeDependency(Address target, in ValueHash256 codeHash)
    {
        List<FrameTxCodeDependency> dependencies = _codeDependencies ??= [];
        foreach (FrameTxCodeDependency dependency in dependencies)
        {
            if (dependency.Account == target) return;
        }

        dependencies.Add(new FrameTxCodeDependency(target, codeHash));
    }

    internal enum CodeMutability : byte
    {
        None,
        ViaDelegation,
        Direct,
    }

    /// <summary>EIP-8298 code mutability verdicts, keyed by code hash and whether EIP-8024 immediates are skipped.</summary>
    internal static readonly ClockCache<(ValueHash256, bool), CodeMutability> MutableCodeCache =
        new(4_096, comparer: EqualityComparer<(ValueHash256, bool)>.Default);

    /// <summary>Classifies <paramref name="code"/> by whether it holds SETCODEFROM, or else DELEGATECALL or CALLCODE, as an instruction.</summary>
    /// <remarks>
    /// Skips PUSH data, and, under EIP-8024, valid DUPN/SWAPN/EXCHANGE immediates. Those are never PUSH or
    /// JUMPDEST bytes, so every jump destination stays an instruction boundary of this scan.
    /// </remarks>
    private static CodeMutability ScanCodeMutability(ReadOnlySpan<byte> code, bool eip8024)
    {
        CodeMutability mutability = CodeMutability.None;
        for (int i = 0; i < code.Length; i++)
        {
            Instruction opcode = (Instruction)code[i];
            switch (opcode)
            {
                case Instruction.SETCODEFROM:
                    return CodeMutability.Direct;
                case Instruction.DELEGATECALL or Instruction.CALLCODE:
                    mutability = CodeMutability.ViaDelegation;
                    break;
                case >= Instruction.PUSH1 and <= Instruction.PUSH32:
                    i += opcode - Instruction.PUSH0;
                    break;
                case Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE when eip8024 && HasEip8024Immediate(code, i):
                    i++;
                    break;
            }
        }

        return mutability;
    }

    /// <summary>Whether the byte after the DUPN/SWAPN/EXCHANGE at <paramref name="i"/> is its valid EIP-8024 immediate, and so not an instruction.</summary>
    private static bool HasEip8024Immediate(ReadOnlySpan<byte> code, int i) =>
        i + 1 < code.Length && ((Instruction)code[i] == Instruction.EXCHANGE
            ? EvmInstructions.IsValidEip8024PairImmediate(code[i + 1])
            : EvmInstructions.IsValidEip8024SingleImmediate(code[i + 1]));

    /// <summary>EIP-8272: <c>RECENT_ROOT_CODE</c> may read the keys its own frame's tuples derive, and nothing else.</summary>
    private bool IsRecentRootKeyRead(Address address, in UInt256 storageIndex) =>
        _inRecentRootCode && address == Eip8272Constants.RecentRootAddress && _recentRootKeys!.Contains(storageIndex);

    private static HashSet<UInt256> RecentRootKeys(ReadOnlySpan<byte> tuples)
    {
        HashSet<UInt256> keys = [];
        for (int offset = 0; offset < tuples.Length; offset += Eip8272Constants.RecentRootTupleLength)
        {
            (ValueHash256 sourceId, ulong slot, _) = RecentRootStore.ReadTuple(tuples.Slice(offset));
            keys.Add(RecentRootStore.ReferenceCell(sourceId, slot).Index);
        }

        return keys;
    }

    private static bool IsCall(Instruction opcode) => opcode is
        Instruction.CALL or Instruction.CALLCODE or Instruction.DELEGATECALL or Instruction.STATICCALL;
}
