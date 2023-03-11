// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
