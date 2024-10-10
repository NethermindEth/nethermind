// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public class Secp256r1Precompile : IPrecompile<Secp256r1Precompile>
{
    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);

    public static readonly Secp256r1Precompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3450L;
    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    // TODO can be optimized - Go implementation is 2-6 times faster depending on the platform. Options:
    // - Try to replicate Go version in C#
    // - Compile Go code into a library and call it via P/Invoke
    public (ReadOnlyMemory<byte>, bool) Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length != 160)
            return (null, true);

        ReadOnlySpan<byte> bytes = inputData.Span;
        ReadOnlySpan<byte> hash = bytes[..32], sig = bytes[32..96];
        ReadOnlySpan<byte> x = bytes[96..128], y = bytes[128..160];

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new() { X = x.ToArray(), Y = y.ToArray() }
        });
        var isValid = ecdsa.VerifyHash(hash, sig);

        Metrics.Secp256r1Precompile++;

        return (isValid ? ValidResult : null, true);
    }
}
