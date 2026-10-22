// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Crypto
{
    public class ProtectedPrivateKeyFactory(ICryptoRandom random, ITimestamper timestamper, string keyStoreDir) : IProtectedPrivateKeyFactory
    {
        private readonly ICryptoRandom _random = random;
        private readonly ITimestamper _timestamper = timestamper;
        private readonly string _keyStoreDir = keyStoreDir;

        public ProtectedPrivateKey Create(PrivateKey privateKey) => new(privateKey, _keyStoreDir, _random, _timestamper);
    }
}
