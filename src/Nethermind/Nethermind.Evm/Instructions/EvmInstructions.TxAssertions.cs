// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// EIP-7906 transaction-assertion opcodes: TXTRACE, TXDIFF and EVENTDATACOPY. All three are valid
/// only inside a POST_TX frame; anywhere else they exceptional-halt. They read the transaction's
/// state diff from the in-flight BAL slice and its logs from the shared frame-transaction log buffer.
/// Each opcode multiplexes on a leading <c>param</c> byte and routes to a per-category helper.
/// https://eips.ethereum.org/EIPS/eip-7906
/// </summary>
/// <remarks>
/// Unused stack inputs: the spec's parameter tables mark certain <c>in2</c>/<c>in3</c> operands as
/// "must be 0" but do not define the consequence of a non-zero value. We treat a violation as an
/// exceptional halt (the strict reading of a normative constraint); this is an interpretation to be
/// confirmed against the reference tests / the EIP author, and is cheap to relax if they ignore it.
/// </remarks>
public static unsafe partial class EvmInstructions
{
    /// <summary>TXTRACE (0xb5): enumerate the transaction's state diff and events by index.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTxTrace<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetPostTxView(vm, out TransactionDiffView view, out FrameTxContext ctx)) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<TxTraceGasCost>(ref gas);
        if (!stack.PopUInt256(out UInt256 param, out UInt256 index)) return EvmExceptionType.StackUnderflow;
        if (param > 0x15) return EvmExceptionType.BadInstruction;

        // Count/scalar params take no index; the spec marks in2 "must be 0" (see the type remarks).
        byte p = (byte)param.u0;
        return p switch
        {
            0x00 => Count<TTracingInst>(in index, view.BalanceAddresses.Length, ref stack),
            0x01 => Count<TTracingInst>(in index, view.Slots.Length, ref stack),
            0x02 => Count<TTracingInst>(in index, view.DeployedAddresses.Length, ref stack),
            >= 0x03 and <= 0x05 => TxTraceBalance<TTracingInst>(view, p, in index, ref stack),
            >= 0x06 and <= 0x09 => TxTraceStorage<TTracingInst>(view, p, in index, ref stack),
            0x0A or 0x0B => TxTraceDeployment<TTracingInst>(view, p, in index, ref stack),
            0x0C => Count<TTracingInst>(in index, view.Logs.Length, ref stack),
            >= 0x0D and <= 0x13 => TxTraceEvent<TTracingInst>(view, p, in index, ref stack),
            0x14 => index.IsZero ? stack.PushUInt256<TTracingInst>(ctx.MaxCost) : EvmExceptionType.BadInstruction,
            0x15 => index.IsZero ? stack.PushAddress<TTracingInst>(ctx.Payer ?? Address.Zero) : EvmExceptionType.BadInstruction,
            _ => EvmExceptionType.BadInstruction,
        };
    }

    private static EvmExceptionType Count<TTracingInst>(in UInt256 index, int count, ref EvmStack stack)
        where TTracingInst : struct, IFlag
        => index.IsZero
            ? stack.PushUInt256<TTracingInst>((UInt256)(ulong)count)
            : EvmExceptionType.BadInstruction;

    // 0x03 address / 0x04 balance before / 0x05 balance after, indexed by balance-change position.
    private static EvmExceptionType TxTraceBalance<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.BalanceAddresses.Length) return EvmExceptionType.BadInstruction;
        Address address = view.BalanceAddresses[(int)index.u0];
        if (param == 0x03) return stack.PushAddress<TTracingInst>(address);

        AccountChangesAtIndex account = view.Slice.GetAccountChanges(address)!;
        UInt256 value = param == 0x04
            ? account.PreTxBalance ?? default
            : account.BalanceChange?.Value ?? account.PreTxBalance ?? default;
        return stack.PushUInt256<TTracingInst>(value);
    }

    // 0x06 address / 0x07 key / 0x08 value before / 0x09 value after, indexed by slot-change position.
    private static EvmExceptionType TxTraceStorage<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.Slots.Length) return EvmExceptionType.BadInstruction;
        TransactionDiffView.SlotRef slot = view.Slots[(int)index.u0];
        if (param == 0x06) return stack.PushAddress<TTracingInst>(slot.Address);
        if (param == 0x07) return stack.PushUInt256<TTracingInst>(slot.Key);

        AccountChangesAtIndex account = view.Slice.GetAccountChanges(slot.Address)!;
        if (param == 0x08)
            return stack.PushUInt256<TTracingInst>(account.TryGetPreTxStorage(slot.Key, out UInt256 before) ? before : default);
        if (!account.TryGetStorageChange(slot.Key, out StorageChange? change)) return EvmExceptionType.BadInstruction;
        EvmWord after = change.Value.Value;
        return stack.Push32Bytes<TTracingInst>(ref Unsafe.As<EvmWord, byte>(ref after));
    }

    // 0x0A deployed address / 0x0B deployed code hash, indexed by deployment position.
    private static EvmExceptionType TxTraceDeployment<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.DeployedAddresses.Length) return EvmExceptionType.BadInstruction;
        Address address = view.DeployedAddresses[(int)index.u0];
        if (param == 0x0A) return stack.PushAddress<TTracingInst>(address);

        ValueHash256 codeHash = view.Slice.GetAccountChanges(address)!.CodeChange!.Value.CodeHash;
        return stack.Push32Bytes<TTracingInst>(in codeHash);
    }

    // 0x0D address / 0x0E topic count / 0x0F..0x12 topics / 0x13 data length, indexed by event position.
    private static EvmExceptionType TxTraceEvent<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.Logs.Length) return EvmExceptionType.BadInstruction;
        LogEntry log = view.Logs[(int)index.u0];
        switch (param)
        {
            case 0x0D: return stack.PushAddress<TTracingInst>(log.Address);
            case 0x0E: return stack.PushUInt32<TTracingInst>((uint)log.Topics.Length);
            case 0x13: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)log.Data.Length);
            default:
                int topic = param - 0x0F;
                // A missing topic exceptional-halts (the spec states this for topic 0).
                if (topic >= log.Topics.Length) return EvmExceptionType.BadInstruction;
                return stack.PushBytes<TTracingInst>(log.Topics[topic].Bytes);
        }
    }

    /// <summary>TXDIFF (0xb6): keyed access to a single account's diff, warm/cold priced per EIP-2929.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTxDiff<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetPostTxView(vm, out TransactionDiffView view, out _)) return EvmExceptionType.BadInstruction;

        // Spec stack order: param on top, address second, in3 (slot key / local index / unused) third.
        if (!stack.PopUInt256(out UInt256 param)) return EvmExceptionType.StackUnderflow;
        if (param > 0x0A) return EvmExceptionType.BadInstruction;
        Address address = stack.PopAddress(vm.AddressCache);
        if (address is null) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 in3)) return EvmExceptionType.StackUnderflow;

        AccountChangesAtIndex? account = view.Slice.GetAccountChanges(address);
        byte p = (byte)param.u0;
        return p switch
        {
            0x00 or 0x01 => TxDiffStorage<TGasPolicy, TTracingInst>(vm, ref gas, p, address, in in3, account, ref stack),
            // Account params take no in3; the spec marks it "must be 0" (see the type remarks).
            >= 0x02 and <= 0x05 => in3.IsZero
                ? TxDiffAccount<TGasPolicy, TTracingInst>(vm, ref gas, p, view, address, account, ref stack)
                : EvmExceptionType.BadInstruction,
            _ => TxDiffAddressView<TGasPolicy, TTracingInst>(ref gas, p, view, address, in in3, account, ref stack),
        };
    }

    // 0x00 slot value before / 0x01 slot value after, EIP-2929 storage-warm/cold priced.
    // NOTE: the live read below goes through the recording world state, so it appends a storage read to
    // the transaction's BAL slice. The EIP is silent on the EIP-7928 interaction here; whether TXDIFF
    // touches belong in the announced BAL needs pinning against the reference tests (see PR).
    private static EvmExceptionType TxDiffStorage<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref TGasPolicy gas, byte param, Address address, in UInt256 key, AccountChangesAtIndex? account, ref EvmStack stack)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        StorageCell cell = new(address, in key);
        if (!TGasPolicy.ConsumeStorageAccessGas(ref gas, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, in cell, StorageAccessType.SLOAD, vm.Spec))
            return EvmExceptionType.OutOfGas;
        if (param == 0x00 && account is not null && account.TryGetPreTxStorage(key, out UInt256 before))
            return stack.PushUInt256<TTracingInst>(before);
        // "after", or an unmodified slot's "before": the current live value.
        ReadOnlySpan<byte> value = vm.WorldState.Get(in cell);
        return value.Length == 1 && value[0] == 0 ? stack.PushZero<TTracingInst>() : stack.PushBytes<TTracingInst>(value);
    }

    // 0x02 balance before / 0x03 balance after / 0x04 code hash before / 0x05 code hash after,
    // EIP-2929 account-warm/cold priced. Same BAL-recording caveat as TxDiffStorage applies to the live reads.
    private static EvmExceptionType TxDiffAccount<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref TGasPolicy gas, byte param, TransactionDiffView view, Address address, AccountChangesAtIndex? account, ref EvmStack stack)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, vm.Spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address))
            return EvmExceptionType.OutOfGas;
        IWorldState state = vm.WorldState;
        switch (param)
        {
            case 0x02:
                return stack.PushUInt256<TTracingInst>(account?.BalanceChange is not null ? account.PreTxBalance ?? default : state.GetBalance(address));
            case 0x03:
                return stack.PushUInt256<TTracingInst>(state.GetBalance(address));
            case 0x04:
                {
                    ValueHash256 hash = account?.CodeChange is not null ? view.GetPreTxCodeHash(address, account) : state.GetCodeHash(address);
                    return stack.Push32Bytes<TTracingInst>(in hash);
                }
            default:
                {
                    ValueHash256 hash = state.GetCodeHash(address);
                    return stack.Push32Bytes<TTracingInst>(in hash);
                }
        }
    }

    // 0x06..0x0A: per-address views and change flags, flat priced, no access-list interaction.
    private static EvmExceptionType TxDiffAddressView<TGasPolicy, TTracingInst>(ref TGasPolicy gas, byte param, TransactionDiffView view, Address address, in UInt256 in3, AccountChangesAtIndex? account, ref EvmStack stack)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume<TxTraceGasCost>(ref gas);
        // 0x07 / 0x09 take a local index in in3; 0x06 / 0x08 / 0x0A mark it "must be 0" (see the type remarks).
        return param switch
        {
            0x06 => in3.IsZero ? stack.PushUInt256<TTracingInst>((UInt256)(ulong)(account?.StorageChangeCount ?? 0)) : EvmExceptionType.BadInstruction,
            0x07 => view.TryGetSlotRun(address, out int start, out int count) && in3 < (UInt256)(ulong)count
                ? stack.PushUInt256<TTracingInst>((UInt256)(ulong)(start + (int)in3.u0))
                : EvmExceptionType.BadInstruction,
            0x08 => in3.IsZero ? stack.PushUInt256<TTracingInst>((UInt256)(ulong)view.AddressEventCount(address)) : EvmExceptionType.BadInstruction,
            0x09 => view.TryGetAddressEventGlobalIndex(address, in in3, out int global)
                ? stack.PushUInt256<TTracingInst>((UInt256)(ulong)global)
                : EvmExceptionType.BadInstruction,
            _ => in3.IsZero ? stack.PushUInt32<TTracingInst>(ChangeFlags(account)) : EvmExceptionType.BadInstruction, // 0x0A
        };
    }

    /// <summary>EVENTDATACOPY (0xb7): copy a log's non-indexed data into memory (CALLDATACOPY semantics).</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEventDataCopy<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetPostTxView(vm, out TransactionDiffView view, out _)) return EvmExceptionType.BadInstruction;

        // Spec stack order (top to bottom): eventIndex, memOffset, dataOffset, length.
        if (!stack.PopUInt256(out UInt256 eventIndex, out UInt256 memOffset, out UInt256 dataOffset, out UInt256 length))
            goto StackUnderflow;
        if (eventIndex >= (UInt256)(ulong)view.Logs.Length) return EvmExceptionType.BadInstruction;
        byte[] data = view.Logs[(int)eventIndex.u0].Data;

        ulong words = EvmCalculations.Div32Ceiling(in length, out bool outOfGas);
        TGasPolicy.ConsumeDataCopyGas(ref gas, vm.Spec, isExternalCode: false, words);
        if (outOfGas) goto OutOfGas;

        if (UInt256.AddOverflow(length, dataOffset, out UInt256 end) || end > (UInt256)(ulong)data.Length)
            goto AccessViolation;

        if (!length.IsZero)
        {
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in memOffset, length, ref vm.VmState.Memory))
                goto OutOfGas;

            ReadOnlySpan<byte> source = data.AsSpan((int)dataOffset, (int)length);
            vm.VmState.Memory.SaveAfterGas(in memOffset, source);

            if (TTracingInst.IsActive)
            {
                ReadOnlySpan<byte> memoryChange = vm.VmState.Memory.LoadSpanAfterGas(in memOffset, (ulong)length);
                vm.TxTracer.ReportMemoryChange(memOffset, in memoryChange);
            }
        }

        return EvmExceptionType.None;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    AccessViolation:
        return EvmExceptionType.AccessViolation;
    }

    /// <summary>
    /// Resolves the shared POST_TX diff view for the current frame transaction, building and caching it
    /// on first use. Returns false — an exceptional halt for the caller — outside a POST_TX frame or when
    /// the diff source is unavailable.
    /// </summary>
    /// <remarks>
    /// The diff source is the EIP-7928 recording world state (<see cref="IBlockAccessListSource"/>), which
    /// is only constructed on the block-processing path. On read-only re-execution paths that run on a
    /// plain world state (eth_call, eth_estimateGas, debug/trace re-execution) the cast fails and these
    /// opcodes halt, so a transaction valid in a block reports as failed when simulated. Making them work
    /// there needs a non-BAL diff source and is left as a follow-up; activation should also be tied to the
    /// EIP-7928 transition rather than <c>IsEip7906Enabled</c> alone (a chainspec enabling 7906 without
    /// 7928 gets opcodes that always halt).
    /// </remarks>
    private static bool TryGetPostTxView<TGasPolicy>(VirtualMachine<TGasPolicy> vm, out TransactionDiffView view, out FrameTxContext ctx)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        view = null!;
        ctx = null!;
        FrameTxContext? frameContext = vm.TxExecutionContext.FrameTxContext;
        if (frameContext is null || frameContext.CurrentFrame.Mode != TxFrame.ModePostTx) return false;
        ctx = frameContext;

        if (frameContext.PostTxDiffView is TransactionDiffView cached)
        {
            view = cached;
            return true;
        }

        if (vm.WorldState is not IBlockAccessListSource source || source.GeneratedBlockAccessList is not { } slice)
            return false;

        view = TransactionDiffView.Build(slice, vm.VmState.AccessTracker.Logs.ToArray());
        frameContext.PostTxDiffView = view;
        return true;
    }

    // EIP-7906 change-flags bitmask: nonce (0b0001), balance (0b0010), storage (0b0100), code (0b1000).
    // CAVEAT: balance/storage/code are true net differences (the BAL nulls them when the value returns to
    // its pre-tx value), but the BAL never nulls a nonce change, so the nonce bit means "nonce was written"
    // rather than "differs from prestate". Exact net-diff nonce semantics would need a pre-tx nonce record.
    private static uint ChangeFlags(AccountChangesAtIndex? account)
    {
        if (account is null) return 0;
        uint flags = 0;
        if (account.NonceChange is not null) flags |= 0b0001;
        if (account.BalanceChange is not null) flags |= 0b0010;
        if (account.StorageChangeCount > 0) flags |= 0b0100;
        if (account.CodeChange is not null) flags |= 0b1000;
        return flags;
    }
}
