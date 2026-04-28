// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-7951" />
/// </summary>
public partial class SecP256r1Precompile : IPrecompile<SecP256r1Precompile>
{
    private static readonly byte[] _successResult = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];

    public static SecP256r1Precompile Instance { get; } = new();

    public static Address Address { get; } = Address.FromNumber(0x100);

    public static string Name => "P256VERIFY";

    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip7951Enabled ? 6900L : 3450L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) => 0L;

    // should produce empty valid output for all invalid-length inputs
    public ReadOnlyMemory<byte> NormalizeInput(ReadOnlyMemory<byte> inputData) =>
        inputData.Length == 160 ? inputData : ReadOnlyMemory<byte>.Empty;

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);
}
