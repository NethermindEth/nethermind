// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public class ProtectedPrivateKey(PrivateKey privateKey, string keyStoreDir,
        ICryptoRandom? random = null, ITimestamper? timestamper = null) : ProtectedData<PrivateKey>(privateKey.KeyBytes, keyStoreDir, random, timestamper), IProtectedPrivateKey
    {
        protected override PrivateKey CreateUnprotected(byte[] data) => new(data);

        public PublicKey PublicKey { get; } = privateKey.PublicKey;

        public CompressedPublicKey CompressedPublicKey { get; } = privateKey.CompressedPublicKey;

        public Address Address => PublicKey.Address;
    }
}
