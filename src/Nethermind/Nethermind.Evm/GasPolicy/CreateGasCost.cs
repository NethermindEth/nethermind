// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm.GasPolicy;

/// <summary>The opcode-dependent parts of the CREATE-family charge, shared by every gas policy.</summary>
/// <remarks>
/// Each variant states its properties on <see cref="EvmInstructions.IOpCreate"/>, so a new variant is priced by
/// declaring them rather than by another type comparison here.
/// </remarks>
internal static class CreateGasCost
{
    /// <summary>The base charge, before init-code words and memory.</summary>
    /// <remarks>EIP-8360: <c>TCREATE</c> pays <c>BASE_OPCODE_COST</c> instead of the account-creation charge.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Base<TOpCreate>(bool eip8038, bool eip8037) where TOpCreate : struct, EvmInstructions.IOpCreate =>
        TOpCreate.IsTransient ? GasCostOf.TCreate
        : eip8038 ? Eip8038Constants.CreateAccess
        : eip8037 ? GasCostOf.CreateExecution
        : GasCostOf.Create;

    /// <summary>The EIP-1014 init-code hashing charge, paid by the salted variants only.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong InitCodeHashing<TOpCreate>(ulong initCodeWords) where TOpCreate : struct, EvmInstructions.IOpCreate =>
        TOpCreate.IsSalted ? GasCostOf.Sha3Word * initCodeWords : 0;
}
