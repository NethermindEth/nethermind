// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Crypto;

public static class Ripemd
{
    const int HashOutputLength = 32;

    // The BouncyCastle digest is stateful but cheap to reuse: DoFinal resets it for the next hash.
    // One instance per thread avoids allocating the digest plus its internal buffers on every call.
    [ThreadStatic]
    private static RipeMD160Digest? _digest;

    public static byte[] Compute(ReadOnlySpan<byte> input)
    {
        RipeMD160Digest digest = _digest ??= new();
        digest.BlockUpdate(input);
        byte[] result = new byte[HashOutputLength];
        int length = digest.GetDigestSize();
        Span<byte> span = result.AsSpan(HashOutputLength - length, length);
        digest.DoFinal(span);
        return result;
    }

    public static string ComputeString(ReadOnlySpan<byte> input) => Compute(input).ToHexString(false);
}
