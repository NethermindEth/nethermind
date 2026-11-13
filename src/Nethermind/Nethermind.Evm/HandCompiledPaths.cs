// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static class HandCompiledPaths
{
    public static bool Enabled;
    public static ValueHash256 WethCodeHash;
    public static ulong WethTransferStaticGas;
    public static long Hits;

    private const uint TransferSelector = 0xa9059cbb;
    private const byte WethBalanceSlot = 3;
    private static readonly UInt256 TransferTopic =
        new(Convert.FromHexString("ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"), isBigEndian: true);

    internal static bool TryRun<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, out EvmExceptionType result)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        result = EvmExceptionType.None;
        VmState<TGasPolicy> state = vm.VmState;
        ExecutionEnvironment env = state.Env;
        if (state.ProgramCounter != 0 || state.IsStatic || env.CodeInfo.CodeHash != WethCodeHash) return false;
        ReadOnlySpan<byte> input = env.InputData.Span;
        if (input.Length < 68 || BinaryPrimitives.ReadUInt32BigEndian(input) != TransferSelector || !env.Value.IsZero) return false;
        if (input.Slice(4, 12).IndexOfAnyExcept((byte)0) >= 0) return false;
        Hits++;
        result = WethTransfer(vm, ref stack, ref gas, env.Caller, new Address(input.Slice(16, Address.Size)), new UInt256(input.Slice(36, 32), isBigEndian: true));
        return true;
    }

    private static EvmExceptionType WethTransfer<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, Address src, Address dst, UInt256 wad)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (!TGasPolicy.UpdateGas(ref gas, WethTransferStaticGas)) return EvmExceptionType.OutOfGas;

        UInt256 srcSlot = BalanceSlot(src);
        UInt256 dstSlot = BalanceSlot(dst);

        EvmExceptionType r = SLoad(vm, ref stack, ref gas, in srcSlot, out UInt256 srcBalance);
        if (r != EvmExceptionType.None) return r;
        if (srcBalance < wad)
        {
            stack.PushUInt256<OffFlag>(UInt256.Zero);
            stack.PushUInt256<OffFlag>(UInt256.Zero);
            return EvmInstructions.InstructionRevert<TGasPolicy, OffFlag>(ref stack, ref gas, vm);
        }

        r = SStore(vm, ref stack, ref gas, in srcSlot, srcBalance - wad);
        if (r != EvmExceptionType.None) return r;

        r = SLoad(vm, ref stack, ref gas, in dstSlot, out UInt256 dstBalance);
        if (r != EvmExceptionType.None) return r;
        r = SStore(vm, ref stack, ref gas, in dstSlot, dstBalance + wad);
        if (r != EvmExceptionType.None) return r;

        stack.PushUInt256<OffFlag>(in wad);
        stack.PushUInt256<OffFlag>(UInt256.Zero);
        r = EvmInstructions.InstructionMStore<TGasPolicy, OffFlag>(ref stack, ref gas, vm);
        if (r != EvmExceptionType.None) return r;

        stack.PushAddress<OffFlag>(dst);
        stack.PushAddress<OffFlag>(src);
        stack.PushUInt256<OffFlag>(in TransferTopic);
        stack.PushUInt256<OffFlag>(new UInt256(32));
        stack.PushUInt256<OffFlag>(UInt256.Zero);
        r = EvmInstructions.InstructionLog<TGasPolicy, EvmInstructions.Op3>(ref stack, ref gas, vm);
        if (r != EvmExceptionType.None) return r;

        stack.PushUInt256<OffFlag>(UInt256.One);
        stack.PushUInt256<OffFlag>(UInt256.Zero);
        r = EvmInstructions.InstructionMStore<TGasPolicy, OffFlag>(ref stack, ref gas, vm);
        if (r != EvmExceptionType.None) return r;

        stack.PushUInt256<OffFlag>(new UInt256(32));
        stack.PushUInt256<OffFlag>(UInt256.Zero);
        return EvmInstructions.InstructionReturn<TGasPolicy, OffFlag>(ref stack, ref gas, vm);
    }

    private static EvmExceptionType SLoad<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, in UInt256 slot, out UInt256 value)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        value = default;
        stack.PushUInt256<OffFlag>(in slot);
        EvmExceptionType r = EvmInstructions.InstructionSLoad<TGasPolicy, OffFlag, Eip8038Off, OnFlag, OffFlag>(ref stack, ref gas, vm);
        if (r != EvmExceptionType.None) return r;
        stack.PopUInt256(out value);
        return EvmExceptionType.None;
    }

    private static EvmExceptionType SStore<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, in UInt256 slot, in UInt256 value)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        stack.PushUInt256<OffFlag>(in value);
        stack.PushUInt256<OffFlag>(in slot);
        return EvmInstructions.InstructionSStoreMetered<TGasPolicy, OffFlag, OnFlag, OffFlag, Eip8038Off, OnFlag, OffFlag>(ref stack, ref gas, vm);
    }

    private static UInt256 BalanceSlot(Address owner)
    {
        Span<byte> buffer = stackalloc byte[64];
        buffer.Clear();
        owner.Bytes.CopyTo(buffer.Slice(12, Address.Size));
        buffer[63] = WethBalanceSlot;
        ValueHash256 hash = ValueKeccak.Compute(buffer);
        return new UInt256(hash.Bytes, isBigEndian: true);
    }
}
