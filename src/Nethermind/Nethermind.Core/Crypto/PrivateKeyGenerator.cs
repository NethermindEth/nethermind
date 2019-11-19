/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Secp256k1;

namespace Nethermind.Core.Crypto
{
    public class PrivateKeyGenerator : IPrivateKeyGenerator
    {
        private ICryptoRandom _cryptoRandom;
        
        public PrivateKeyGenerator()
        {
            _cryptoRandom = new CryptoRandom();
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
    }
}