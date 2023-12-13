// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Crypto
{
    public static class Ripemd
    {
        public static byte[] Compute(byte[] input)
        {
            var digest = new RipeMD160Digest();
            digest.BlockUpdate(input, 0, input.Length);
            var result = new byte[digest.GetDigestSize()];
            digest.DoFinal(result, 0);
            return result;
        }

        public static string ComputeString(byte[] input)
        {
            return Compute(input).ToHexString(false);
        }
    }
}
