// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles.Snarks;

/// <summary>
/// https://github.com/herumi/mcl/blob/master/api.md
/// </summary>
public class Bn254PairingPrecompile : IPrecompile
{
    private const int PairSize = 192;

    public static IPrecompile Instance = new Bn254PairingPrecompile();

    public static Address Address { get; } = Address.FromNumber(8);

    public long BaseGasCost(IReleaseSpec releaseSpec)
    {
        return releaseSpec.IsEip1108Enabled ? 45000L : 100000L;
    }

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return (releaseSpec.IsEip1108Enabled ? 34000L : 80000L) * (inputData.Length / PairSize);
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Bn254PairingPrecompile++;

        (byte[], bool) result;
        if (inputData.Length % PairSize > 0)
        {
            // note that it will not happen in case of null / 0 length
            result = (Array.Empty<byte>(), false);
        }
        else
        {
            /* we modify input in place here and this is save for EVM but not
               safe in benchmarks so we need to remember to clone */
            Span<byte> output = stackalloc byte[64];
            Span<byte> inputDataSpan = inputData.ToArray().AsSpan();
            Span<byte> inputReshuffled = stackalloc byte[PairSize];
            for (int i = 0; i < inputData.Length / PairSize; i++)
            {
                inputDataSpan.Slice(i * PairSize + 0, 64).CopyTo(inputReshuffled[..64]);
                inputDataSpan.Slice(i * PairSize + 64, 32).CopyTo(inputReshuffled.Slice(96, 32));
                inputDataSpan.Slice(i * PairSize + 96, 32).CopyTo(inputReshuffled.Slice(64, 32));
                inputDataSpan.Slice(i * PairSize + 128, 32).CopyTo(inputReshuffled.Slice(160, 32));
                inputDataSpan.Slice(i * PairSize + 160, 32).CopyTo(inputReshuffled.Slice(128, 32));
                inputReshuffled.CopyTo(inputDataSpan.Slice(i * PairSize, PairSize));
            }

            bool success = Pairings.Bn254Pairing(inputDataSpan, output);

            if (success)
            {
                result = (output[..32].ToArray(), true);
            }
            else
            {
                result = (Array.Empty<byte>(), false);
            }
        }

        return result;
    }
}
