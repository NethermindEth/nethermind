// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Precompiles;

public class Sha256Precompile : IPrecompile<Sha256Precompile>
{
    public static readonly Sha256Precompile Instance = new();

    private Sha256Precompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(2);

    public static string Name => "SHA256";

    public long BaseGasCost(IReleaseSpec releaseSpec) => 60L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        12L * EvmInstructions.Div32Ceiling((ulong)inputData.Length);

    public (byte[], bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Metrics.Sha256Precompile++;

        byte[] output = new byte[SHA256.HashSizeInBytes];
        bool success = SHA256.TryHashData(inputData.Span, output, out int bytesWritten);

        return (output, success && bytesWritten == SHA256.HashSizeInBytes);
    }
}
