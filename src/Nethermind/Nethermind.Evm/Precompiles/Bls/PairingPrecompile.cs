// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class PairingPrecompile : IPrecompile<PairingPrecompile>
{
    private const int PairSize = 384;

    private PairingPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x11);

    public static PairingPrecompile Instance = new PairingPrecompile();

    public long BaseGasCost(IReleaseSpec releaseSpec) => 65000L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 43000L * (inputData.Length / PairSize);

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length % PairSize > 0 || inputData.Length == 0)
        {
            return IPrecompile.Failure;
        }

        for (int i = 0; i < (inputData.Length / PairSize); i++)
        {
            int offset = i * PairSize;
            if (!SubgroupChecks.G1IsInSubGroup(inputData.Span[offset..(offset + (2 * BlsParams.LenFp))]))
            {
                return IPrecompile.Failure;
            }

            offset += 2 * BlsParams.LenFp;

            if (!SubgroupChecks.G2IsInSubGroup(inputData.Span[offset..(offset + (4 * BlsParams.LenFp))]))
            {
                return IPrecompile.Failure;
            }
        }

        Span<byte> output = stackalloc byte[32];
        return Pairings.BlsPairing(inputData.Span, output) ? (output.ToArray(), true) : IPrecompile.Failure;
    }
}
