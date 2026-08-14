// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Crypto;

public sealed class PrivateKeyGenerator : IPrivateKeyGenerator, IDisposable
{
    private readonly ICryptoRandom _cryptoRandom;
    private readonly bool _disposeRandom = false;

    public PrivateKeyGenerator()
    {
        _cryptoRandom = new CryptoRandom();
        _disposeRandom = true;
    }

    public PrivateKeyGenerator(ICryptoRandom cryptoRandom) => _cryptoRandom = cryptoRandom;

    public PrivateKey Generate()
    {
        do
        {
            byte[] bytes = _cryptoRandom.GenerateRandomBytes(32);
            if (SecP256k1.VerifyPrivateKey(bytes))
            {
                return new PrivateKey(bytes);
            }
        } while (true);
    }

    public IEnumerable<PrivateKey> Generate(int number)
    {
        do
        {
            yield return Generate();
            if (--number == 0)
            {
                yield break;
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
