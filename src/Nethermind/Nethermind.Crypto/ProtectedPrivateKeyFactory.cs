// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Crypto
{
    public class ProtectedPrivateKeyFactory : IProtectedPrivateKeyFactory
    {
        private readonly ICryptoRandom _random;
        private readonly ITimestamper _timestamper;

        public ProtectedPrivateKeyFactory(ICryptoRandom random, ITimestamper timestamper)
        {
            _random = random;
            _timestamper = timestamper;
        }

        public ProtectedPrivateKey Create(PrivateKey privateKey) => new(privateKey, _random, _timestamper);
    }
}
