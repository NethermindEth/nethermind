// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.State;

namespace Nethermind.Evm.Precompiles.Bls;

/// <summary>
/// https://eips.ethereum.org/EIPS/eip-2537
/// </summary>
public class PairingPrecompile : IPrecompile<PairingPrecompile>
{
    private const int PairSize = 384;

    private PairingPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x12);

    public static PairingPrecompile Instance = new PairingPrecompile();

    public long BaseGasCost(IReleaseSpec releaseSpec) => 115000L;

    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        return 23000L * (inputData.Length / PairSize);
    }

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, IWorldState _)
    {
        if (inputData.Length % PairSize > 0 || inputData.Length == 0)
        {
            return (Array.Empty<byte>(), false);
        }

        (byte[], bool) result;

        Span<byte> output = stackalloc byte[32];
        bool success = Pairings.BlsPairing(inputData.Span, output);
        if (success)
        {
            result = (output.ToArray(), true);
        }
        else
        {
            result = (Array.Empty<byte>(), false);
        }

        return result;
    }
}
