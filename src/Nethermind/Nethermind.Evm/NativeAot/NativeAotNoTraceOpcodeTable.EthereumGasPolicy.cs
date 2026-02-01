// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.NativeAot;

/// <summary>
/// NativeAOT-only helper for precomputing the EVM opcode table (no-trace) for <see cref="VirtualMachine{TGasPolicy}"/>
/// instantiated with <see cref="EthereumGasPolicy"/>.
///
/// Under NativeAOT, runtime generation of opcode tables via generic function pointers can be unavailable
/// (e.g., when <c>RuntimeFeature.IsDynamicCodeSupported</c> is false). This class provides a compiled-in
/// opcode table by delegating to <see cref="EvmInstructions.GenerateOpCodes{TGasPolicy, TTracingInst}"/> with
/// <see cref="OffFlag"/> tracing.
///
/// Notes:
/// - This works only if the AOT toolchain can statically compile the required generic instantiations.
/// - The returned table is cached per-spec instance to allow different forks/features to select different opcode handlers.
/// </summary>
internal static unsafe class NativeAotNoTraceOpcodeTableEthereumGasPolicy
{
    // We cache per-spec reference because opcode availability depends on spec feature flags (e.g., EOF, PUSH0, etc.).
    // In StatelessExecution the spec object is typically reused; this avoids reallocating the 256-entry table.
    private static IReleaseSpec? _cachedSpec;
    private static delegate*<VirtualMachine<EthereumGasPolicy>, ref EvmStack, ref EthereumGasPolicy, ref int, EvmExceptionType>[]? _cachedTable;

    /// <summary>
    /// Tries to get a precomputed (compiled-in) no-trace opcode table for the provided <paramref name="spec"/>.
    /// </summary>
    /// <param name="spec">Release spec that determines opcode feature flags.</param>
    /// <returns>
    /// A typed opcode table matching <see cref="VirtualMachine{TGasPolicy}"/> with <see cref="EthereumGasPolicy"/>,
    /// or <c>null</c> if it could not be created.
    /// </returns>
    public static delegate*<VirtualMachine<EthereumGasPolicy>, ref EvmStack, ref EthereumGasPolicy, ref int, EvmExceptionType>[]? TryGet(IReleaseSpec spec)
    {
        if (ReferenceEquals(spec, _cachedSpec))
        {
            return _cachedTable;
        }

        // If this method is reachable under NativeAOT, the call below must be AOT-compilable.
        // If it isn't, the build/link step should fail, which is preferable to a runtime crash.
        delegate*<VirtualMachine<EthereumGasPolicy>, ref EvmStack, ref EthereumGasPolicy, ref int, EvmExceptionType>[] table =
            EvmInstructions.GenerateOpCodes<EthereumGasPolicy, OffFlag>(spec);

        _cachedSpec = spec;
        _cachedTable = table;
        return table;
    }

    /// <summary>
    /// Returns the table as <see cref="Array"/> for assignment into <see cref="IReleaseSpec.EvmInstructionsNoTrace"/>.
    /// This avoids leaking the unsafe function-pointer element type across APIs that store it as <see cref="Array"/>.
    /// </summary>
    public static Array? TryGetAsArray(IReleaseSpec spec)
    {
        delegate*<VirtualMachine<EthereumGasPolicy>, ref EvmStack, ref EthereumGasPolicy, ref int, EvmExceptionType>[]? table = TryGet(spec);
        return table;
    }
}
