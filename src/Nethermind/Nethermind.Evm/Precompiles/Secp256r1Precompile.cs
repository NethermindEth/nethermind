// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

public class Secp256r1Precompile : IPrecompile<Secp256r1Precompile>
{
    private const int RequiredInputLength = 160;
    private static readonly byte[] ValidResult = new byte[] { 1 }.PadLeft(32);
    private static readonly byte[] InvalidResult = new byte[] { 0 }.PadLeft(32);

    public static readonly Secp256r1Precompile Instance = new();
    public static Address Address { get; } = Address.FromNumber(0x100);

    public long BaseGasCost(IReleaseSpec releaseSpec) => 3450L;
    public long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0L;

    public (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length != RequiredInputLength)
            return (Array.Empty<byte>(), false);

        ReadOnlySpan<byte> bytes = inputData.Span;
        ReadOnlySpan<byte> hash = bytes[..32], sig = bytes[32..96];
        ReadOnlySpan<byte> x = bytes[96..128], y = bytes[128..160];

        // TODO optimize
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new() { X = x.ToArray(), Y = y.ToArray() }
        });
        var isValid = ecdsa.VerifyHash(hash, sig);

        Metrics.Secp256r1Precompile++;

        return (isValid ? ValidResult : InvalidResult, true);
    }
}
