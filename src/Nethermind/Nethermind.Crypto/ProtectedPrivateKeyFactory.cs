// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Crypto
{
    public class ProtectedPrivateKeyFactory : IProtectedPrivateKeyFactory
    {
        private readonly ICryptoRandom _random;
        private readonly ITimestamper _timestamper;
        private readonly string _keyStoreDir;

        public ProtectedPrivateKeyFactory(ICryptoRandom random, ITimestamper timestamper, string keyStoreDir)
        {
            _random = random;
            _timestamper = timestamper;
            _keyStoreDir = keyStoreDir;
        }

        public ProtectedPrivateKey Create(PrivateKey privateKey) => new(privateKey, _keyStoreDir, _random, _timestamper);
    }
}
