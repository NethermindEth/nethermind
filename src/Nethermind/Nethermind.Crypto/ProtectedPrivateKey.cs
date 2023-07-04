// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public class ProtectedPrivateKey : ProtectedData<PrivateKey>
    {
        public ProtectedPrivateKey(PrivateKey privateKey, string keyStoreDir,
            ICryptoRandom? random = null, ITimestamper? timestamper = null)
            : base(privateKey.KeyBytes, keyStoreDir, random, timestamper)
        {
            PublicKey = privateKey.PublicKey;
            CompressedPublicKey = privateKey.CompressedPublicKey;
        }

        protected override PrivateKey CreateUnprotected(byte[] data) => new(data);

        public PublicKey PublicKey { get; }

        public CompressedPublicKey CompressedPublicKey { get; }

        public Address Address => PublicKey.Address;
    }
}
