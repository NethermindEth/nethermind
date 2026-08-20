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
/// EIP-7906 transaction-assertion opcodes, valid only inside a POST_TX frame.
/// https://eips.ethereum.org/EIPS/eip-7906
/// </summary>
/// <remarks>Operands the spec marks "must be 0" are reserved: a non-zero value exceptional-halts.</remarks>
public static unsafe partial class EvmInstructions
{
    /// <summary>TXTRACE (0xb6): enumerate the transaction's state diff and events by index.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTxTrace<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetPostTxView(vm, out TransactionDiffView view, out FrameTxContext ctx)) return EvmExceptionType.BadInstruction;

        TGasPolicy.Consume<TxTraceGasCost>(ref gas);
        if (!stack.PopUInt256(out UInt256 param, out UInt256 index)) return EvmExceptionType.StackUnderflow;
        if (param > 0x15) return EvmExceptionType.BadInstruction;

        byte p = (byte)param.u0;
        return p switch
        {
            0x00 => PushCountForZeroIndex<TTracingInst>(in index, view.BalanceAddresses.Length, ref stack),
            0x01 => PushCountForZeroIndex<TTracingInst>(in index, view.Slots.Length, ref stack),
            0x02 => PushCountForZeroIndex<TTracingInst>(in index, view.DeployedAddresses.Length, ref stack),
            >= 0x03 and <= 0x05 => TxTraceBalance<TTracingInst>(view, p, in index, ref stack),
            >= 0x06 and <= 0x09 => TxTraceStorage<TTracingInst>(view, p, in index, ref stack),
            0x0A or 0x0B => TxTraceDeployment<TTracingInst>(view, p, in index, ref stack),
            0x0C => PushCountForZeroIndex<TTracingInst>(in index, view.Logs.Length, ref stack),
            >= 0x0D and <= 0x13 => TxTraceEvent<TTracingInst>(view, p, in index, ref stack),
            0x14 => index.IsZero ? stack.PushUInt256<TTracingInst>(ctx.MaxCost) : EvmExceptionType.BadInstruction,
            // Unapproved payment is rejected after the frame loop, so no valid transaction sees this zero.
            0x15 => index.IsZero ? stack.PushAddress<TTracingInst>(ctx.Payer ?? Address.Zero) : EvmExceptionType.BadInstruction,
            _ => EvmExceptionType.BadInstruction,
        };
    }

    private static EvmExceptionType PushCountForZeroIndex<TTracingInst>(in UInt256 index, int count, ref EvmStack stack)
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
                if (topic >= log.Topics.Length) return EvmExceptionType.BadInstruction; // missing topic halts
                return stack.PushBytes<TTracingInst>(log.Topics[topic].Bytes);
        }
    }

    /// <summary>TXDIFF (0xb7): keyed access to a single account's diff, warm/cold priced per EIP-2929.</summary>
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
            >= 0x02 and <= 0x05 => in3.IsZero
                ? TxDiffAccount<TGasPolicy, TTracingInst>(vm, ref gas, p, view, address, account, ref stack)
                : EvmExceptionType.BadInstruction,
            _ => TxDiffAddressView<TGasPolicy, TTracingInst>(ref gas, p, view, address, in in3, account, ref stack),
        };
    }

    // 0x00 before / 0x01 after. The live read enters the EIP-7928 list like any other state read.
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

    // 0x02/0x03 balance, 0x04/0x05 code hash; live reads are EIP-7928 recorded as above.
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
        return param switch
        {
            0x06 => in3.IsZero ? stack.PushUInt256<TTracingInst>((UInt256)(ulong)(view.TryGetSlotRun(address, out _, out int slots) ? slots : 0)) : EvmExceptionType.BadInstruction,
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

    /// <summary>EVENTDATACOPY (0xb8): copy a log's non-indexed data into memory. Gas is CALLDATACOPY-shaped,
    /// but an out-of-range read halts rather than zero-padding.</summary>
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

    /// <summary>Diff view for the current frame tx, built once and shared. False outside a POST_TX
    /// frame, or when no EIP-7928 slice is recording to source it from.</summary>
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

    // The nonce bit is "written", not net: nonces only increase and a revert restores the prior value.
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
