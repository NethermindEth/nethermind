// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Cortex.SimpleSerialize;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2.Cryptography.Ssz
{
    public static class BlsSignatureExtensions
    {
        public static SszElement ToSszBasicVector(this BlsSignature item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}
