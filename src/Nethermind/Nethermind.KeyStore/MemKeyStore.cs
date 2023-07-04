// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private readonly string _ketStoreDir;

        public MemKeyStore(PrivateKey[] privateKeys, string ketStoreDir)
        {
            _privateKeys =
                new Dictionary<Address, PrivateKey>(privateKeys.Select(pk =>
                    new KeyValuePair<Address, PrivateKey>(pk.Address, pk)));
            _ketStoreDir = ketStoreDir;
        }

        public (KeyStoreItem KeyData, Result Result) Verify(string keyJson)
        {
            throw new System.NotImplementedException();
        }

        public (PrivateKey PrivateKey, Result Result) GetKey(Address address, SecureString password)
        {
            return _privateKeys.TryGetValue(address, out PrivateKey value) ? (value, Result.Success) : (null, Result.Fail("Can't unlock key."));
        }

        public (ProtectedPrivateKey PrivateKey, Result Result) GetProtectedKey(Address address, SecureString password)
        {
            return _privateKeys.TryGetValue(address, out PrivateKey value)
                ? (new ProtectedPrivateKey(value, _ketStoreDir), Result.Success)
                : (null, Result.Fail("Can't unlock key."));
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
