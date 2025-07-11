// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles.Snarks;

public class Bn254PairingPrecompile : IPrecompile<Bn254PairingPrecompile>
{
    private const int Bn256PairingMaxInputSizeGranite = 112_687;

    public static readonly Bn254PairingPrecompile Instance = new();

    public static Address Address { get; } = Address.FromNumber(8);

    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip1108Enabled ? 45_000L : 100_000L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        (releaseSpec.IsEip1108Enabled ? 34_000L : 80_000L) * (inputData.Length / BN254.PairSize);

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254PairingPrecompile++;

        if (releaseSpec.IsOpGraniteEnabled && inputData.Length > Bn256PairingMaxInputSizeGranite ||
            inputData.Length % BN254.PairSize > 0)
        {
            return IPrecompile.Failure;
        }

        byte[] input = ArrayPool<byte>.Shared.Rent(inputData.Length);
        Span<byte> output = stackalloc byte[32];

        inputData.CopyTo(input);

        bool result = BN254.Pairing(input.AsSpan(0, inputData.Length), output);

        ArrayPool<byte>.Shared.Return(input);

        return result ? (output.ToArray(), true) : IPrecompile.Failure;
    }
}
