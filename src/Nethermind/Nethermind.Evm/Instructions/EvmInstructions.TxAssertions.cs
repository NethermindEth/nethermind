// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// EIP-7906 transaction-assertion opcodes, valid only inside an ASSERT frame.
/// https://eips.ethereum.org/EIPS/eip-7906
/// </summary>
/// <remarks>Operands the spec marks "must be 0" are reserved: a non-zero value exceptional-halts.</remarks>
public static partial class EvmInstructions
{
    /// <summary>TXTRACE (0xb6): enumerate the transaction's state diff and events by index.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTxTrace<TGasPolicy, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetAssertFrameView(vm, out TransactionDiffView view, out FrameTxContext ctx)) return EvmExceptionType.BadInstruction;

        if (!TGasPolicy.UpdateGas<TxTraceGasCost>(ref gas)) return EvmExceptionType.OutOfGas;
        if (!stack.PopUInt256(out UInt256 param, out UInt256 index)) return EvmExceptionType.StackUnderflow;
        if (param > 0x1A) return EvmExceptionType.BadInstruction;

        byte p = (byte)param.u0;
        return p switch
        {
            0x00 => PushCountForZeroIndex<TTracingInst>(in index, view.BalanceAddresses.Length, ref stack),
            0x01 => PushCountForZeroIndex<TTracingInst>(in index, view.Slots.Length, ref stack),
            0x02 => PushCountForZeroIndex<TTracingInst>(in index, view.CodeAddresses.Length, ref stack),
            0x03 => PushCountForZeroIndex<TTracingInst>(in index, view.NonceAddresses.Length, ref stack),
            >= 0x04 and <= 0x06 => TxTraceBalance<TTracingInst>(view, p, in index, ref stack),
            >= 0x07 and <= 0x0A => TxTraceStorage<TTracingInst>(view, p, in index, ref stack),
            >= 0x0B and <= 0x0D => TxTraceCode<TTracingInst>(view, p, in index, ref stack),
            >= 0x0E and <= 0x10 => TxTraceNonce<TTracingInst>(view, p, in index, ref stack),
            0x11 => PushCountForZeroIndex<TTracingInst>(in index, view.Logs.Length, ref stack),
            >= 0x12 and <= 0x18 => TxTraceEvent<TTracingInst>(view, p, in index, ref stack),
            0x19 => index.IsZero ? stack.PushUInt256<TTracingInst>(ctx.MaxCost) : EvmExceptionType.BadInstruction,
            // Unapproved payment is rejected after the frame loop, so no valid transaction sees this zero.
            0x1A => index.IsZero ? stack.PushAddress<TTracingInst>(ctx.Payer ?? Address.Zero) : EvmExceptionType.BadInstruction,
            _ => EvmExceptionType.BadInstruction,
        };
    }

    private static EvmExceptionType PushCountForZeroIndex<TTracingInst>(in UInt256 index, int count, ref EvmStack stack)
        where TTracingInst : struct, IFlag
        => index.IsZero
            ? stack.PushUInt256<TTracingInst>((UInt256)(ulong)count)
            : EvmExceptionType.BadInstruction;

    /// <summary>0x04 address / 0x05 balance before / 0x06 balance after, indexed by balance-change position.</summary>
    private static EvmExceptionType TxTraceBalance<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.BalanceAddresses.Length) return EvmExceptionType.BadInstruction;
        Address address = view.BalanceAddresses[(int)index.u0];
        if (param == 0x04) return stack.PushAddress<TTracingInst>(address);

        AccountChangesAtIndex account = view.Slice.GetAccountChanges(address)!;
        UInt256 value = param == 0x05
            ? account.PreTxBalance ?? default
            : account.BalanceChange?.Value ?? account.PreTxBalance ?? default;
        return stack.PushUInt256<TTracingInst>(value);
    }

    /// <summary>0x07 address / 0x08 value before / 0x09 value after / 0x0A key, indexed by slot-change position.</summary>
    private static EvmExceptionType TxTraceStorage<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.Slots.Length) return EvmExceptionType.BadInstruction;
        TransactionDiffView.SlotRef slot = view.Slots[(int)index.u0];
        if (param == 0x07) return stack.PushAddress<TTracingInst>(slot.Address);
        if (param == 0x0A) return stack.PushUInt256<TTracingInst>(slot.Key);

        AccountChangesAtIndex account = view.Slice.GetAccountChanges(slot.Address)!;
        if (param == 0x08)
            return stack.PushUInt256<TTracingInst>(account.TryGetPreTxStorage(slot.Key, out UInt256 before) ? before : default);
        if (!account.StorageChanges.TryGetValue(slot.Key, out StorageChange change)) return EvmExceptionType.BadInstruction;
        return stack.PushUInt256<TTracingInst>(change.Value);
    }

    /// <summary>0x0B address / 0x0C code hash before / 0x0D code hash after, indexed by code-change position.</summary>
    private static EvmExceptionType TxTraceCode<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.CodeAddresses.Length) return EvmExceptionType.BadInstruction;
        Address address = view.CodeAddresses[(int)index.u0];
        if (param == 0x0B) return stack.PushAddress<TTracingInst>(address);

        AccountChangesAtIndex account = view.Slice.GetAccountChanges(address)!;
        ValueHash256 codeHash = param == 0x0C ? view.GetPreTxCodeHash(address, account) : account.CodeChange!.Value.CodeHash;
        return stack.Push32Bytes<TTracingInst, OnFlag>(in codeHash);
    }

    /// <summary>0x0E address / 0x0F nonce before / 0x10 nonce after, indexed by nonce-change position.</summary>
    private static EvmExceptionType TxTraceNonce<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.NonceAddresses.Length) return EvmExceptionType.BadInstruction;
        Address address = view.NonceAddresses[(int)index.u0];
        if (param == 0x0E) return stack.PushAddress<TTracingInst>(address);

        AccountChangesAtIndex account = view.Slice.GetAccountChanges(address)!;
        ulong nonce = param == 0x0F ? account.PreTxNonce ?? 0 : account.NonceChange!.Value.Value;
        return stack.PushUInt256<TTracingInst>(nonce);
    }

    /// <summary>0x12 address / 0x13 topic count / 0x14..0x17 topics / 0x18 data length, indexed by event position.</summary>
    private static EvmExceptionType TxTraceEvent<TTracingInst>(TransactionDiffView view, byte param, in UInt256 index, ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        if (index >= (UInt256)(ulong)view.Logs.Length) return EvmExceptionType.BadInstruction;
        LogEntry log = view.Logs[(int)index.u0];
        switch (param)
        {
            case 0x12: return stack.PushAddress<TTracingInst>(log.Address);
            case 0x13: return stack.PushUInt32<TTracingInst, OnFlag>((uint)log.Topics.Length);
            case 0x18: return stack.PushUInt256<TTracingInst>((UInt256)(ulong)log.Data.Length);
            default:
                int topic = param - 0x14;
                if (topic >= log.Topics.Length) return EvmExceptionType.BadInstruction;
                return stack.PushBytes<TTracingInst>(log.Topics[topic].Bytes);
        }
    }

    /// <summary>EVENTDATACOPY (0xb7): copy a log's non-indexed data into memory. Gas is CALLDATACOPY-shaped,
    /// but an out-of-range read halts rather than zero-padding.</summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEventDataCopy<TGasPolicy, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!TryGetAssertFrameView(vm, out TransactionDiffView view, out _)) return EvmExceptionType.BadInstruction;

        // Spec stack order (top to bottom): eventIndex, memOffset, dataOffset, length.
        if (!stack.PopUInt256(out UInt256 eventIndex, out UInt256 memOffset, out UInt256 dataOffset, out UInt256 length))
            return EvmExceptionType.StackUnderflow;
        if (eventIndex >= (UInt256)(ulong)view.Logs.Length) return EvmExceptionType.BadInstruction;

        return BoundedDataCopyCore<TGasPolicy, TTracingInst>(
            vm, ref gas, in memOffset, in dataOffset, in length, view.Logs[(int)eventIndex.u0].Data);
    }

    /// <summary>Diff view for the current frame tx, built once and shared. False outside an ASSERT
    /// frame, or when no EIP-7928 slice is recording to source it from.</summary>
    private static bool TryGetAssertFrameView<TGasPolicy>(VirtualMachine<TGasPolicy> vm, out TransactionDiffView view, out FrameTxContext ctx)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        view = null!;
        ctx = null!;
        FrameTxContext? frameContext = vm.TxExecutionContext.FrameTxContext;
        if (frameContext is null || frameContext.CurrentFrame.Mode != FrameMode.Assert) return false;
        ctx = frameContext;

        if (frameContext.AssertFrameDiffView is TransactionDiffView cached)
        {
            view = cached;
            return true;
        }

        if (vm.WorldState is not IBlockAccessListSource source || source.GeneratedBlockAccessList is not { } slice)
            return false;

        view = TransactionDiffView.Build(slice, vm.VmState.AccessTracker.Logs.ToArray());
        frameContext.AssertFrameDiffView = view;
        return true;
    }
}
