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

using System.Collections.Generic;
using System.Security;
using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.KeyStore
{
    public interface IKeyStore
    {
        (KeyStoreItem KeyData, Result Result) Verify(string keyJson);
        (PrivateKey PrivateKey, Result Result) GetKey(Address address, SecureString password);
        (byte[] Key, Result Result) GetKeyBytes(Address address, SecureString password);
        (ProtectedPrivateKey PrivateKey, Result Result) GetProtectedKey(Address address, SecureString password);
        (KeyStoreItem KeyData, Result Result) GetKeyData(Address address);
        (IReadOnlyCollection<Address> Addresses, Result Result) GetKeyAddresses();
        (PrivateKey PrivateKey, Result Result) GenerateKey(SecureString password);
        (ProtectedPrivateKey PrivateKey, Result Result) GenerateProtectedKey(SecureString password);
        Result StoreKey(Address address, KeyStoreItem keyStoreItem);
        Result StoreKey(PrivateKey key, SecureString password);
        Result StoreKey(Address address, byte[] keyContent, SecureString password);
        Result DeleteKey(Address address);
        int Version { get; }
        int CryptoVersion { get; }
    }
}
