// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

internal sealed partial class EvmInstructions
{
    internal enum StorageAccessType
    {
        SLOAD,
        SSTORE
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionTLoad(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.TransientStorageEnabled) return EvmExceptionType.BadInstruction;

        Metrics.TloadOpcode++;
        gasAvailable -= GasCostOf.TLoad;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);

        ReadOnlySpan<byte> value = vmState.WorldState.GetTransientState(in storageCell);
        stack.PushBytes(value);

        if (vmState.TxTracer.IsTracingStorage)
        {
            if (gasAvailable < 0) return EvmExceptionType.OutOfGas;
            vmState.TxTracer.LoadOperationTransientStorage(storageCell.Address, result, value);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionTStore(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.TransientStorageEnabled) return EvmExceptionType.BadInstruction;

        Metrics.TstoreOpcode++;

        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        gasAvailable -= GasCostOf.TStore;

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);
        Span<byte> bytes = stack.PopWord256();
        vmState.WorldState.SetTransientState(in storageCell, !bytes.IsZero() ? bytes.ToArray() : BytesZero32);
        if (vmState.TxTracer.IsTracingStorage)
        {
            if (gasAvailable < 0) return EvmExceptionType.OutOfGas;
            ReadOnlySpan<byte> currentValue = vmState.WorldState.GetTransientState(in storageCell);
            vmState.TxTracer.SetOperationTransientStorage(storageCell.Address, result, bytes, currentValue);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionMCopy(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vmState.Spec;
        if (!spec.MCopyIncluded) return EvmExceptionType.BadInstruction;

        Metrics.MCopyOpcode++;

        if (!stack.PopUInt256(out UInt256 a)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 b)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 c)) return EvmExceptionType.StackUnderflow;

        gasAvailable -= GasCostOf.VeryLow + GasCostOf.VeryLow * EvmPooledMemory.Div32Ceiling(c);
        if (!UpdateMemoryCost(vmState, ref gasAvailable, UInt256.Max(b, a), c)) return EvmExceptionType.OutOfGas;

        Span<byte> bytes = vmState.Memory.LoadSpan(in b, c);

        var isTracingInstructions = vmState.TxTracer.IsTracingInstructions;
        if (isTracingInstructions) vmState.TxTracer.ReportMemoryChange(b, bytes);
        vmState.Memory.Save(in a, bytes);
        if (isTracingInstructions) vmState.TxTracer.ReportMemoryChange(a, bytes);

        return EvmExceptionType.None;
    }


    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType InstructionSStore(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        Metrics.SstoreOpcode++;
        IReleaseSpec spec = vmState.Spec;
        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;
        // fail fast before the first storage read if gas is not enough even for reset
        if (!spec.UseNetGasMetering && !UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (spec.UseNetGasMeteringWithAStipendFix)
        {
            if (vmState.TxTracer.IsTracingRefunds)
                vmState.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend - spec.GetNetMeteredSStoreCost() + 1);
            if (gasAvailable <= GasCostOf.CallStipend) return EvmExceptionType.OutOfGas;
        }

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        ReadOnlySpan<byte> bytes = stack.PopWord256();
        bool newIsZero = bytes.IsZero();
        bytes = !newIsZero ? bytes.WithoutLeadingZeros() : BytesZero;

        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);

        if (!ChargeStorageAccessGas(
                ref gasAvailable,
                vmState,
                in storageCell,
                StorageAccessType.SSTORE,
                spec)) return EvmExceptionType.OutOfGas;

        ReadOnlySpan<byte> currentValue = vmState.WorldState.Get(in storageCell);
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
                    if (vmState.TxTracer.IsTracingRefunds) vmState.TxTracer.ReportRefund(sClearRefunds);
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
                Span<byte> originalValue = vmState.WorldState.GetOriginal(in storageCell);
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
                            if (vmState.TxTracer.IsTracingRefunds) vmState.TxTracer.ReportRefund(sClearRefunds);
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
                            if (vmState.TxTracer.IsTracingRefunds) vmState.TxTracer.ReportRefund(-sClearRefunds);
                        }

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (vmState.TxTracer.IsTracingRefunds) vmState.TxTracer.ReportRefund(sClearRefunds);
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
                        if (vmState.TxTracer.IsTracingRefunds) vmState.TxTracer.ReportRefund(refundFromReversal);
                    }
                }
            }
        }

        if (!newSameAsCurrent)
        {
            vmState.WorldState.Set(in storageCell, newIsZero ? BytesZero : bytes.ToArray());
        }

        if (vmState.TxTracer.IsTracingInstructions)
        {
            ReadOnlySpan<byte> valueToStore = newIsZero ? BytesZero.AsSpan() : bytes;
            byte[] storageBytes = new byte[32]; // do not stackalloc here
            storageCell.Index.ToBigEndian(storageBytes);
            vmState.TxTracer.ReportStorageChange(storageBytes, valueToStore);
        }

        if (vmState.TxTracer.IsTracingStorage)
        {
            vmState.TxTracer.SetOperationStorage(storageCell.Address, result, bytes, currentValue);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static EvmExceptionType InstructionSLoad(EvmState vmState, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vmState.Spec;
        Metrics.SloadOpcode++;
        gasAvailable -= spec.GetSLoadCost();

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);
        if (!ChargeStorageAccessGas(
            ref gasAvailable,
            vmState,
            in storageCell,
            StorageAccessType.SLOAD,
            spec)) return EvmExceptionType.OutOfGas;

        ReadOnlySpan<byte> value = vmState.WorldState.Get(in storageCell);
        stack.PushBytes(value);
        if (vmState.TxTracer.IsTracingStorage)
        {
            vmState.TxTracer.LoadOperationStorage(storageCell.Address, result, value);
        }

        return EvmExceptionType.None;
    }

    internal static bool ChargeStorageAccessGas(
        ref long gasAvailable,
        EvmState vmState,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
    {
        // Console.WriteLine($"Accessing {storageCell} {storageAccessType}");

        bool result = true;
        if (spec.UseHotAndColdStorage)
        {
            if (vmState.TxTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
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
