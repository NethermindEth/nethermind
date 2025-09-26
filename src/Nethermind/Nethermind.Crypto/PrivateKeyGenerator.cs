// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

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
            return Generate(1).First();
        }
        public IEnumerable<PrivateKey> Generate(int number)
        {
            do
            {
                var bytes = _cryptoRandom.GenerateRandomBytes(32);
                if (SecP256k1.VerifyPrivateKey(bytes))
                {
                    yield return new PrivateKey(bytes);
                    if (--number == 0)
                    {
                        yield break;
                    }
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
