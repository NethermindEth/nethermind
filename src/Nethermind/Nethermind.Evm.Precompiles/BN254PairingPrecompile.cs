// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-197" />
/// </summary>
public partial class BN254PairingPrecompile : IPrecompile<BN254PairingPrecompile>
{
    private const int PairingMaxInputSizeGranite = 112_687;

    public static readonly BN254PairingPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(8);

    /// <see href="https://eips.ethereum.org/EIPS/eip-7910" />
    public static string Name => "BN254_PAIRING";

    /// <see href="https://eips.ethereum.org/EIPS/eip-1108" />
    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 45_000L : 100_000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        (releaseSpec.IsEip1108Enabled ? 34_000L : 80_000L) * (inputData.Length / BN254.PairSize);

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        (!releaseSpec.IsOpGraniteEnabled || inputData.Length <= PairingMaxInputSizeGranite) &&
            inputData.Length % BN254.PairSize == 0;
}
