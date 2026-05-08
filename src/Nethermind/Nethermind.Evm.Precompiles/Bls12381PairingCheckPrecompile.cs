// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public partial class Bls12381PairingCheckPrecompile : IPrecompile<Bls12381PairingCheckPrecompile>
{
    private const int PairSize = 384;

    public static Bls12381PairingCheckPrecompile Instance { get; } = new();

    private Bls12381PairingCheckPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0xf);

    public static string Name => "BLS12_PAIRING_CHECK";

    public long BaseGasCost(IReleaseSpec _) => 37700L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _) => 32600L * (inputData.Length / PairSize);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec _);

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData) =>
        inputData.Length != 0 && inputData.Length % PairSize == 0;
}
