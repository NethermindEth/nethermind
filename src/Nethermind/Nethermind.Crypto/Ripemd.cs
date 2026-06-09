// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Crypto;

public static class Ripemd
{
    const int HashOutputLength = 32;

    [ThreadStatic]
    private static RipeMD160Digest? _digest;

    public static byte[] Compute(ReadOnlySpan<byte> input)
    {
        RipeMD160Digest digest = _digest ??= new();
        try
        {
            byte[] result = new byte[HashOutputLength];
            digest.BlockUpdate(input);
            int length = digest.GetDigestSize();
            Span<byte> span = result.AsSpan(HashOutputLength - length, length);
            digest.DoFinal(span);
            return result;
        }
        finally
        {
            // Reset on every path so an exception between BlockUpdate and DoFinal can't leave the
            // thread-static digest partially fed and corrupt the next call on this thread.
            digest.Reset();
        }
    }

    public static string ComputeString(ReadOnlySpan<byte> input) => Compute(input).ToHexString(false);
}
