// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Cortex.Cryptography;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public class Bls : IBls
    {
        private static readonly BLSApi BlsApi = new(new BLSHerumi(new BLSParameters()));
        public byte[] Sign(PrivateKey privateKey, Hash256 message)
        {
            return BlsApi.Sign(privateKey.KeyBytes, message.Bytes);
        }
    }
}
