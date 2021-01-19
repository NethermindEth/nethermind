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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Crypto;

[assembly: InternalsVisibleTo("Nethermind.KeyStore.Test")]

namespace Nethermind.KeyStore
{
    /// <summary>
    /// For testing only
    /// </summary>
    [DoNotUseInSecuredContext("Untested, also uses lots of unsafe software key generation techniques")]
    public class MemKeyStore : IKeyStore
    {
        private readonly Dictionary<Address, PrivateKey> _privateKeys;

        public MemKeyStore(PrivateKey[] privateKeys)
        {
            _privateKeys = new Dictionary<Address, PrivateKey>(privateKeys.Select(pk => new KeyValuePair<Address, PrivateKey>(pk.Address, pk)));
        }

        public (KeyStoreItem KeyData, Result Result) Verify(string keyJson)
        {
            throw new System.NotImplementedException();
        }

        public (PrivateKey PrivateKey, Result Result) GetKey(Address address, SecureString password)
        {
            return _privateKeys.ContainsKey(address) ? (_privateKeys[address], Result.Success) : (null, Result.Fail("Can't unlock key."));
        }

        public (ProtectedPrivateKey PrivateKey, Result Result) GetProtectedKey(Address address, SecureString password)
        {
            return _privateKeys.ContainsKey(address) ? (new ProtectedPrivateKey(_privateKeys[address]), Result.Success) : (null, Result.Fail("Can't unlock key."));
        }

        public (KeyStoreItem KeyData, Result Result) GetKeyData(Address address)
        {
            throw new System.NotImplementedException();
        }

        public (IReadOnlyCollection<Address> Addresses, Result Result) GetKeyAddresses()
        {
            return (new ReadOnlyCollection<Address>(_privateKeys.Keys.ToList()), Result.Success);
        }

        public (PrivateKey PrivateKey, Result Result) GenerateKey(SecureString password)
        {
            throw new System.NotImplementedException();
        }

        public (ProtectedPrivateKey PrivateKey, Result Result) GenerateProtectedKey(SecureString password)
        {
            throw new System.NotImplementedException();
        }

        public Result StoreKey(Address address, KeyStoreItem keyStoreItem)
        {
            throw new System.NotImplementedException();
        }

        public Result StoreKey(PrivateKey key, SecureString password)
        {
            throw new System.NotImplementedException();
        }

        public Result DeleteKey(Address address)
        {
            throw new System.NotImplementedException();
        }

        public Result StoreKey(Address address, byte[] keyContent, SecureString password)
        {
            throw new NotImplementedException();
        }

        public (byte[] Key, Result Result) GetKeyBytes(Address address, SecureString password)
        {
            throw new NotImplementedException();
        }

        public int Version { get; }
        public int CryptoVersion { get; }
    }
}
