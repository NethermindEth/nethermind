// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Crypto
{
    public class PrivateKeyGenerator : IPrivateKeyGenerator, IDisposable
    {
        private readonly ICryptoRandom _cryptoRandom;
        private readonly bool _disposeRandom = false;

        public PrivateKeyGenerator()
        {
            _cryptoRandom = new CryptoRandom();
            _disposeRandom = true;
        }

        public PrivateKeyGenerator(ICryptoRandom cryptoRandom)
        {
            _cryptoRandom = cryptoRandom;
        }

        public PrivateKey Generate()
        {
            do
            {
                var bytes = _cryptoRandom.GenerateRandomBytes(32);
                if (SecP256k1.VerifyPrivateKey(bytes))
                {
                    return new PrivateKey(bytes);
                }
            } while (true);
        }

        public void Dispose()
        {
            if (_disposeRandom)
            {
                _cryptoRandom.Dispose();
            }
        }
    }
}
