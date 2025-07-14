// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Evm.Precompiles;

public class Secp256r1Precompile : IPrecompile<Secp256r1Precompile>
{
    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

    public static readonly Secp256r1Precompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public static string Name => "P256VERIFY";

    public long BaseGasCost(IReleaseSpec releaseSpec) => releaseSpec.IsEip7951Enabled ? 6900L : 3450L;
    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Secp256r1Precompile++;

        return (
            Secp256r1.VerifySignature(inputData) ? ValidResult : [],
            true
        );
    }
}
