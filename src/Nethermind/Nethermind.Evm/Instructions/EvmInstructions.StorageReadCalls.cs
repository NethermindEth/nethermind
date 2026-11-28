// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// A code whose function <paramref name="Selector"/>, given one 32-byte word, returns its own storage at that word and does
/// nothing else, as Uniswap v4's PoolManager <c>extsload(bytes32)</c> does.
/// </summary>
/// <param name="CodeHash">The code, by hash: no other code is served.</param>
/// <param name="Selector">The function selector, as the call's first four bytes read big-endian.</param>
/// <param name="BaseGas">The gas the function's frame uses besides the SLOAD's warm or cold charge.</param>
/// <param name="Unrecorded">
/// Whether the read leaves a slot the transaction has not written out of the block's changes
/// (<see cref="State.IWorldState.GetUnrecorded"/>), which spares a read-only call recording slots it only looks at.
/// </param>
public readonly record struct StorageReadCall(ValueHash256 CodeHash, uint Selector, ulong BaseGas, bool Unrecorded = false);

/// <summary>
/// Codes whose storage-read function a STATICCALL runs as the SLOAD it is, without a child frame: the call charges and
/// returns what the frame would, and changes nothing else. Plugins register the codes they vouch for at startup.
/// </summary>
/// <remarks>
/// Whoever registers a code vouches that its function reads only that slot of its own storage, returns it as one word and
/// uses <see cref="StorageReadCall.BaseGas"/> besides the SLOAD under the specs the node runs; the machine serves the call
/// only where storage access is priced warm or cold (EIP-2929) and none of EIPs 8038, 8279 or 8360 changes the charges.
/// </remarks>
public static class StorageReadCalls
{
    private static readonly Lock _lock = new();
    private static StorageReadCall[] _calls = [];

    /// <summary>Whether any code is registered, which every STATICCALL checks first.</summary>
    public static bool Any
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _calls.Length != 0;
    }

    /// <summary>Registers <paramref name="call"/>; a code and selector registered already keep their first registration.</summary>
    public static void Register(in StorageReadCall call)
    {
        lock (_lock)
        {
            StorageReadCall[] calls = _calls;
            foreach (StorageReadCall known in calls)
            {
                if (known.CodeHash == call.CodeHash && known.Selector == call.Selector) return;
            }

            Volatile.Write(ref _calls, [.. calls, call]);
        }
    }

    /// <summary>Forgets every registration; for tests.</summary>
    internal static void Clear()
    {
        lock (_lock) Volatile.Write(ref _calls, []);
    }

    internal static bool TryGet(in ValueHash256 codeHash, uint selector, out StorageReadCall call)
    {
        foreach (StorageReadCall known in _calls)
        {
            if (known.Selector == selector && known.CodeHash == codeHash)
            {
                call = known;
                return true;
            }
        }

        call = default;
        return false;
    }

    internal static bool HasCode(in ValueHash256 codeHash)
    {
        foreach (StorageReadCall known in _calls)
        {
            if (known.CodeHash == codeHash) return true;
        }

        return false;
    }
}

public static partial class EvmInstructions
{
    /// <summary>
    /// Runs a STATICCALL of a registered storage-read function (<see cref="StorageReadCalls"/>) as its SLOAD: charges the
    /// frame's base gas and the SLOAD's warm or cold charge from the call's gas, reads the slot, and returns the word as the
    /// frame would, in the return data and the output area; false, with nothing done, for any call it does not serve.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryServeStorageReadCall<TGasPolicy, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        in UInt256 dataOffset,
        in UInt256 dataLength,
        in UInt256 outputOffset,
        in UInt256 outputLength,
        CodeInfo codeInfo,
        Address target,
        ulong gasLimitUl,
        out EvmExceptionType result)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        result = default;
        // Traced calls run their frame, as do calls that could run out of gas part way: the frame needs at most the base
        // and a cold SLOAD.
        if (TTracingInst.IsActive || vm.IsTracingActions || vm.IsTracingAccess || vm.IsTracingOpLevelStorage ||
            !(dataLength.IsUint64 && dataLength.u0 == 36) || !StorageReadCalls.HasCode(codeInfo.CodeHash))
            return false;

        ReadOnlySpan<byte> input = vm.VmState.Memory.LoadSpanAfterGas(in dataOffset, in dataLength);
        if (!StorageReadCalls.TryGet(codeInfo.CodeHash, BinaryPrimitives.ReadUInt32BigEndian(input), out StorageReadCall call) ||
            gasLimitUl < call.BaseGas + GasCostOf.ColdSLoad)
            return false;

        StorageCell cell = new(target, new UInt256(input.Slice(4, 32), isBigEndian: true));
        TGasPolicy childGas = TGasPolicy.CreateChildFrameGas(ref gas, gasLimitUl);
        TGasPolicy.UpdateGas(ref childGas, call.BaseGas);
        TGasPolicy.TryConsumeStorageAccessGas(ref childGas, in vm.VmState.AccessTracker, in cell, StorageAccessType.SLOAD, vm.Spec);
        vm.MetricsCounters.IncrementSLoad();
        UInt256 value;
        if (call.Unrecorded) vm.WorldState.GetUnrecorded(in cell, out value);
        else vm.WorldState.Get(in cell, out value);
        TGasPolicy.Refund(ref gas, in childGas);

        Span<byte> word = stackalloc byte[32];
        value.ToBigEndian(word);
        // RETURNDATACOPY may read the output after this frame writes memory again, so it cannot live there.
        Memory<byte> output = vm.CopyToPrecompileScratch(word);
        vm.ReturnDataBuffer = output;
        int copyLength = outputLength.IsUint64 && outputLength.u0 < 32 ? (int)outputLength.u0 : 32;
        vm.VmState.Memory.SaveAfterGas(in outputOffset, output.Span[..copyLength]);

        result = stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
        return true;
    }
}
