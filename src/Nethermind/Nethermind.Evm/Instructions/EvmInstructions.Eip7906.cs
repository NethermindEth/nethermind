// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// EIP-7906 transaction-assertion opcodes: TXTRACE, TXDIFF and EVENTDATACOPY. All three are valid
/// only inside a POST_TX frame; anywhere else they exceptional-halt. They read the transaction's
/// state diff from the in-flight BAL slice and its logs from the shared frame-transaction log buffer.
/// https://eips.ethereum.org/EIPS/eip-7906
/// </summary>
public static unsafe partial class EvmInstructions
{
    /// <summary>TXTRACE (0xb5): enumerate the transaction's state diff and events by index.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTxTrace<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetPostTxView(vm, out Eip7906DiffView view, out FrameTxContext ctx)) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<TxTraceGasCost>(ref gas);
        if (!stack.PopUInt256(out UInt256 param, out UInt256 index)) return EvmExceptionType.StackUnderflow;
        if (param > 0x15) return EvmExceptionType.BadInstruction;

        BlockAccessListAtIndex slice = view.Slice;
        switch (param.u0)
        {
            case 0x00: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)view.BalanceAddresses.Length);
            case 0x01: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)view.Slots.Length);
            case 0x02: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)view.DeployedAddresses.Length);

            case 0x03: // balance-change address
            case 0x04: // balance before
            case 0x05: // balance after (as of the opcode call; equal to the recorded post-value)
            {
                if (index >= (UInt256)(ulong)view.BalanceAddresses.Length) return EvmExceptionType.BadInstruction;
                Address address = view.BalanceAddresses[(int)index.u0];
                if (param.u0 == 0x03) return stack.PushAddress<TTracingInst>(address);
                AccountChangesAtIndex account = slice.GetAccountChanges(address)!;
                UInt256 value = param.u0 == 0x04
                    ? account.PreTxBalance ?? default
                    : account.BalanceChange?.Value ?? account.PreTxBalance ?? default;
                return stack.PushUInt256<TTracingInst>(value);
            }

            case 0x06: // slot-change address
            case 0x07: // slot key
            case 0x08: // slot value before
            case 0x09: // slot value after
            {
                if (index >= (UInt256)(ulong)view.Slots.Length) return EvmExceptionType.BadInstruction;
                Eip7906DiffView.SlotRef slot = view.Slots[(int)index.u0];
                if (param.u0 == 0x06) return stack.PushAddress<TTracingInst>(slot.Address);
                if (param.u0 == 0x07) return stack.PushUInt256<TTracingInst>(slot.Key);
                AccountChangesAtIndex account = slice.GetAccountChanges(slot.Address)!;
                if (param.u0 == 0x08)
                    return stack.PushUInt256<TTracingInst>(account.TryGetPreTxStorage(slot.Key, out UInt256 before) ? before : default);
                if (!account.TryGetStorageChange(slot.Key, out StorageChange? change)) return EvmExceptionType.BadInstruction;
                EvmWord after = change.Value.Value;
                return stack.Push32Bytes<TTracingInst>(ref Unsafe.As<EvmWord, byte>(ref after));
            }

            case 0x0A: // deployed contract address
            case 0x0B: // deployed contract code hash
            {
                if (index >= (UInt256)(ulong)view.DeployedAddresses.Length) return EvmExceptionType.BadInstruction;
                Address address = view.DeployedAddresses[(int)index.u0];
                if (param.u0 == 0x0A) return stack.PushAddress<TTracingInst>(address);
                ValueHash256 codeHash = slice.GetAccountChanges(address)!.CodeChange!.Value.CodeHash;
                return stack.Push32Bytes<TTracingInst>(in codeHash);
            }

            case 0x0C: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)view.Logs.Length);

            case 0x0D: // event address
            case 0x0E: // event topic count
            case 0x0F: // event topic 0..3 (0x0F..0x12)
            case 0x10:
            case 0x11:
            case 0x12:
            case 0x13: // event data length
            {
                if (index >= (UInt256)(ulong)view.Logs.Length) return EvmExceptionType.BadInstruction;
                LogEntry log = view.Logs[(int)index.u0];
                switch (param.u0)
                {
                    case 0x0D: return stack.PushAddress<TTracingInst>(log.Address);
                    case 0x0E: return stack.PushUInt32<TTracingInst>((uint)log.Topics.Length);
                    case 0x13: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)log.Data.Length);
                    default:
                        int topic = (int)(param.u0 - 0x0F);
                        // A missing topic exceptional-halts (the spec states this for topic 0).
                        if (topic >= log.Topics.Length) return EvmExceptionType.BadInstruction;
                        return stack.PushBytes<TTracingInst>(log.Topics[topic].Bytes);
                }
            }

            case 0x14: return stack.PushUInt256<TTracingInst>(ctx.MaxCost);
            case 0x15: return stack.PushAddress<TTracingInst>(ctx.Payer ?? Address.Zero);

            default: return EvmExceptionType.BadInstruction;
        }
    }

    /// <summary>TXDIFF (0xb6): keyed access to a single account's diff, warm/cold priced per EIP-2929.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTxDiff<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetPostTxView(vm, out Eip7906DiffView view, out _)) return EvmExceptionType.BadInstruction;

        // Spec stack order: param on top, address second, in3 (slot key / local index / unused) third.
        if (!stack.PopUInt256(out UInt256 param)) return EvmExceptionType.StackUnderflow;
        if (param > 0x0A) return EvmExceptionType.BadInstruction;
        Address address = stack.PopAddress(vm.AddressCache);
        if (address is null) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 in3)) return EvmExceptionType.StackUnderflow;

        IReleaseSpec spec = vm.Spec;
        IWorldState state = vm.WorldState;
        AccountChangesAtIndex? account = view.Slice.GetAccountChanges(address);

        switch (param.u0)
        {
            case 0x00: // slot value before
            case 0x01: // slot value after
            {
                StorageCell cell = new(address, in in3);
                if (!TGasPolicy.ConsumeStorageAccessGas(ref gas, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, in cell, StorageAccessType.SLOAD, spec))
                    return EvmExceptionType.OutOfGas;
                if (param.u0 == 0x00 && account is not null && account.TryGetPreTxStorage(in3, out UInt256 before))
                    return stack.PushUInt256<TTracingInst>(before);
                // "after", or an unmodified slot's "before": the current live value.
                ReadOnlySpan<byte> value = state.Get(in cell);
                return value.Length == 1 && value[0] == 0 ? stack.PushZero<TTracingInst>() : stack.PushBytes<TTracingInst>(value);
            }

            case 0x02: // balance before
            case 0x03: // balance after
            case 0x04: // code hash before
            case 0x05: // code hash after
            {
                if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address))
                    return EvmExceptionType.OutOfGas;
                switch (param.u0)
                {
                    case 0x02:
                        return stack.PushUInt256<TTracingInst>(account?.BalanceChange is not null ? account.PreTxBalance ?? default : state.GetBalance(address));
                    case 0x03:
                        return stack.PushUInt256<TTracingInst>(state.GetBalance(address));
                    case 0x04:
                    {
                        ValueHash256 hash = account?.CodeChange is not null ? ValueKeccak.Compute(account.PreTxCode ?? []) : state.GetCodeHash(address);
                        return stack.Push32Bytes<TTracingInst>(in hash);
                    }
                    default:
                    {
                        ValueHash256 hash = state.GetCodeHash(address);
                        return stack.Push32Bytes<TTracingInst>(in hash);
                    }
                }
            }

            default: // 0x06..0x0A: per-address views and flags, flat priced, no access-list interaction.
            {
                TGasPolicy.Consume<TxTraceGasCost>(ref gas);
                switch (param.u0)
                {
                    case 0x06: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)(account?.StorageChangeCount ?? 0));
                    case 0x07: return PushGlobalSlotIndex<TTracingInst>(view, address, in3, ref stack);
                    case 0x08: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)CountEventsFrom(view, address));
                    case 0x09: return PushGlobalEventIndex<TTracingInst>(view, address, in3, ref stack);
                    default: return stack.PushUInt32<TTracingInst>(ChangeFlags(account)); // 0x0A
                }
            }
        }
    }

    /// <summary>EVENTDATACOPY (0xb7): copy a log's non-indexed data into memory (CALLDATACOPY semantics).</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEventDataCopy<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetPostTxView(vm, out Eip7906DiffView view, out _)) return EvmExceptionType.BadInstruction;

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
    /// BAL recording is not active (the diff source is unavailable).
    /// </summary>
    private static bool TryGetPostTxView<TGasPolicy>(VirtualMachine<TGasPolicy> vm, out Eip7906DiffView view, out FrameTxContext ctx)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        view = null!;
        ctx = null!;
        FrameTxContext? frameContext = vm.TxExecutionContext.FrameTxContext;
        if (frameContext is null || frameContext.CurrentFrame.Mode != TxFrame.ModePostTx) return false;
        ctx = frameContext;

        if (frameContext.PostTxDiffView is Eip7906DiffView cached)
        {
            view = cached;
            return true;
        }

        if (vm.WorldState is not IBlockAccessListSource source || source.GeneratedBlockAccessList is not { } slice)
            return false;

        view = Eip7906DiffView.Build(slice, vm.VmState.AccessTracker.Logs.ToArray());
        frameContext.PostTxDiffView = view;
        return true;
    }

    private static EvmExceptionType PushGlobalSlotIndex<TTracingInst>(Eip7906DiffView view, Address address, UInt256 localIndex, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        Eip7906DiffView.SlotRef[] slots = view.Slots;
        int first = -1, count = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (!slots[i].Address.Equals(address)) continue;
            if (first < 0) first = i;
            count++;
        }
        if (localIndex >= (UInt256)(ulong)count) return EvmExceptionType.BadInstruction;
        return stack.PushUInt256<TTracingInst>((UInt256)(ulong)(first + (int)localIndex.u0));
    }

    private static EvmExceptionType PushGlobalEventIndex<TTracingInst>(Eip7906DiffView view, Address address, UInt256 localIndex, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        LogEntry[] logs = view.Logs;
        int local = 0;
        for (int i = 0; i < logs.Length; i++)
        {
            if (!logs[i].Address.Equals(address)) continue;
            if (localIndex == (UInt256)(ulong)local) return stack.PushUInt256<TTracingInst>((UInt256)(ulong)i);
            local++;
        }
        return EvmExceptionType.BadInstruction;
    }

    private static int CountEventsFrom(Eip7906DiffView view, Address address)
    {
        int count = 0;
        foreach (LogEntry log in view.Logs) if (log.Address.Equals(address)) count++;
        return count;
    }

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
