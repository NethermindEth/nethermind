// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    internal enum StorageAccessType
    {
        SLOAD,
        SSTORE
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionTLoad(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        Metrics.TloadOpcode++;
        gasAvailable -= GasCostOf.TLoad;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vm.State.Env.ExecutingAccount, result);

        ReadOnlySpan<byte> value = vm.WorldState.GetTransientState(in storageCell);
        stack.PushBytes(value);

        if (vm.TxTracer.IsTracingStorage)
        {
            if (gasAvailable < 0) return EvmExceptionType.OutOfGas;
            vm.TxTracer.LoadOperationTransientStorage(storageCell.Address, result, value);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionTStore(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        Metrics.TstoreOpcode++;
        EvmState vmState = vm.State;

        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        gasAvailable -= GasCostOf.TStore;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);
        Span<byte> bytes = stack.PopWord256();
        vm.WorldState.SetTransientState(in storageCell, !bytes.IsZero() ? bytes.ToArray() : BytesZero32);
        if (vm.TxTracer.IsTracingStorage)
        {
            if (gasAvailable < 0) return EvmExceptionType.OutOfGas;
            ReadOnlySpan<byte> currentValue = vm.WorldState.GetTransientState(in storageCell);
            vm.TxTracer.SetOperationTransientStorage(storageCell.Address, result, bytes, currentValue);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMCopy(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        Metrics.MCopyOpcode++;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 c)) return EvmExceptionType.StackUnderflow;

        gasAvailable -= GasCostOf.VeryLow + GasCostOf.VeryLow * EvmPooledMemory.Div32Ceiling(c);
        EvmState vmState = vm.State;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, UInt256.Max(b, a), c)) return EvmExceptionType.OutOfGas;

        Span<byte> bytes = vmState.Memory.LoadSpan(in b, c);

        var isTracingInstructions = vm.TxTracer.IsTracingInstructions;
        if (isTracingInstructions) vm.TxTracer.ReportMemoryChange(b, bytes);
        vmState.Memory.Save(in a, bytes);
        if (isTracingInstructions) vm.TxTracer.ReportMemoryChange(a, bytes);

        return EvmExceptionType.None;
    }


    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType InstructionSStore(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        Metrics.IncrementSStoreOpcode();
        EvmState vmState = vm.State;
        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;
        IReleaseSpec spec = vm.Spec;
        // fail fast before the first storage read if gas is not enough even for reset
        if (!spec.UseNetGasMetering && !UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (spec.UseNetGasMeteringWithAStipendFix)
        {
            if (vm.TxTracer.IsTracingRefunds)
                vm.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend - spec.GetNetMeteredSStoreCost() + 1);
            if (gasAvailable <= GasCostOf.CallStipend) return EvmExceptionType.OutOfGas;
        }

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        ReadOnlySpan<byte> bytes = stack.PopWord256();
        bool newIsZero = bytes.IsZero();
        bytes = !newIsZero ? bytes.WithoutLeadingZeros() : BytesZero;

        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);

        if (!ChargeStorageAccessGas(
                ref gasAvailable,
                vm,
                in storageCell,
                StorageAccessType.SSTORE,
                spec)) return EvmExceptionType.OutOfGas;

        ReadOnlySpan<byte> currentValue = vm.WorldState.Get(in storageCell);
        // Console.WriteLine($"current: {currentValue.ToHexString()} newValue {newValue.ToHexString()}");
        bool currentIsZero = currentValue.IsZero();

        bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, bytes);
        long sClearRefunds = RefundOf.SClear(spec.IsEip3529Enabled);

        if (!spec.UseNetGasMetering) // note that for this case we already deducted 5000
        {
            if (newIsZero)
            {
                if (!newSameAsCurrent)
                {
                    vmState.Refund += sClearRefunds;
                    if (vm.TxTracer.IsTracingRefunds) vm.TxTracer.ReportRefund(sClearRefunds);
                }
            }
            else if (currentIsZero)
            {
                if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable)) return EvmExceptionType.OutOfGas;
            }
        }
        else // net metered
        {
            if (newSameAsCurrent)
            {
                if (!UpdateGas(spec.GetNetMeteredSStoreCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;
            }
            else // net metered, C != N
            {
                Span<byte> originalValue = vm.WorldState.GetOriginal(in storageCell);
                bool originalIsZero = originalValue.IsZero();

                bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);
                if (currentSameAsOriginal)
                {
                    if (currentIsZero)
                    {
                        if (!UpdateGas(GasCostOf.SSet, ref gasAvailable)) return EvmExceptionType.OutOfGas;
                    }
                    else // net metered, current == original != new, !currentIsZero
                    {
                        if (!UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (vm.TxTracer.IsTracingRefunds) vm.TxTracer.ReportRefund(sClearRefunds);
                        }
                    }
                }
                else // net metered, new != current != original
                {
                    long netMeteredStoreCost = spec.GetNetMeteredSStoreCost();
                    if (!UpdateGas(netMeteredStoreCost, ref gasAvailable)) return EvmExceptionType.OutOfGas;

                    if (!originalIsZero) // net metered, new != current != original != 0
                    {
                        if (currentIsZero)
                        {
                            vmState.Refund -= sClearRefunds;
                            if (vm.TxTracer.IsTracingRefunds) vm.TxTracer.ReportRefund(-sClearRefunds);
                        }

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (vm.TxTracer.IsTracingRefunds) vm.TxTracer.ReportRefund(sClearRefunds);
                        }
                    }

                    bool newSameAsOriginal = Bytes.AreEqual(originalValue, bytes);
                    if (newSameAsOriginal)
                    {
                        long refundFromReversal;
                        if (originalIsZero)
                        {
                            refundFromReversal = spec.GetSetReversalRefund();
                        }
                        else
                        {
                            refundFromReversal = spec.GetClearReversalRefund();
                        }

                        vmState.Refund += refundFromReversal;
                        if (vm.TxTracer.IsTracingRefunds) vm.TxTracer.ReportRefund(refundFromReversal);
                    }
                }
            }
        }

        if (!newSameAsCurrent)
        {
            vm.WorldState.Set(in storageCell, newIsZero ? BytesZero : bytes.ToArray());
        }

        if (vm.TxTracer.IsTracingInstructions)
        {
            ReadOnlySpan<byte> valueToStore = newIsZero ? BytesZero.AsSpan() : bytes;
            byte[] storageBytes = new byte[32]; // do not stackalloc here
            storageCell.Index.ToBigEndian(storageBytes);
            vm.TxTracer.ReportStorageChange(storageBytes, valueToStore);
        }

        if (vm.TxTracer.IsTracingStorage)
        {
            vm.TxTracer.SetOperationStorage(storageCell.Address, result, bytes, currentValue);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType InstructionSLoad(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        Metrics.IncrementSLoadOpcode();
        gasAvailable -= spec.GetSLoadCost();

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vm.State.Env.ExecutingAccount, result);
        if (!ChargeStorageAccessGas(
            ref gasAvailable,
            vm,
            in storageCell,
            StorageAccessType.SLOAD,
            spec)) return EvmExceptionType.OutOfGas;

        ReadOnlySpan<byte> value = vm.WorldState.Get(in storageCell);
        stack.PushBytes(value);
        if (vm.TxTracer.IsTracingStorage)
        {
            vm.TxTracer.LoadOperationStorage(storageCell.Address, result, value);
        }

        return EvmExceptionType.None;
    }

    internal static bool ChargeStorageAccessGas(
        ref long gasAvailable,
        IEvm vm,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
    {
        EvmState vmState = vm.State;
        bool result = true;
        if (spec.UseHotAndColdStorage)
        {
            if (vm.TxTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
            {
                vmState.WarmUp(in storageCell);
            }

            if (vmState.IsCold(in storageCell))
            {
                result = UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
                vmState.WarmUp(in storageCell);
            }
            else if (storageAccessType == StorageAccessType.SLOAD)
            {
                // we do not charge for WARM_STORAGE_READ_COST in SSTORE scenario
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
        }

        return result;
    }
}
