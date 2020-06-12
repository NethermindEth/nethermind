//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Secp256k1;

namespace Nethermind.Wallet
{
    public class ProtectedBasicWallet : IBasicWallet
    {
        private readonly ProtectedPrivateKey _privateKey;

        public ProtectedBasicWallet(PrivateKey privateKey)
        {
            _privateKey = new ProtectedPrivateKey(privateKey ?? throw new ArgumentNullException(nameof(privateKey)));
        }

        public ProtectedBasicWallet(ProtectedPrivateKey privateKey)
        {
            _privateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
        }

        public Signature Sign(Keccak message, Address address)
        {
            using var key = _privateKey.Unprotect();
            var rs = Proxy.SignCompact(message.Bytes, key.KeyBytes, out int v);
            return new Signature(rs, v);
        }
    }
}
