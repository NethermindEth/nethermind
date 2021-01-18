//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Secp256k1;

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
                if (Proxy.VerifyPrivateKey(bytes))
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
