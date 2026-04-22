// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Security;
using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.KeyStore;

public class NullKeyStore : IKeyStore
{
    public static IKeyStore Instance { get; } = new NullKeyStore();

    public int Version => 0;
    public int CryptoVersion => 0;

    public (KeyStoreItem KeyData, Result Result) Verify(string keyJson) =>
        (null!, Result.Fail("null keystore"));

    public (PrivateKey PrivateKey, Result Result) GetKey(Address address, SecureString password) =>
        (null!, Result.Fail("null keystore"));

    public (byte[] Key, Result Result) GetKeyBytes(Address address, SecureString password) =>
        (null!, Result.Fail("null keystore"));

    public (ProtectedPrivateKey PrivateKey, Result Result) GetProtectedKey(Address address, SecureString password) =>
        (null!, Result.Fail("null keystore"));

    public (KeyStoreItem KeyData, Result Result) GetKeyData(Address address) =>
        (null!, Result.Fail("null keystore"));

    public (IReadOnlyCollection<Address> Addresses, Result Result) GetKeyAddresses() =>
        ([], Result.Success);

    public (PrivateKey PrivateKey, Result Result) GenerateKey(SecureString password) =>
        (null!, Result.Fail("null keystore"));

    public (ProtectedPrivateKey PrivateKey, Result Result) GenerateProtectedKey(SecureString password) =>
        (null!, Result.Fail("null keystore"));

    public Result StoreKey(Address address, KeyStoreItem keyStoreItem) => Result.Success;
    public Result StoreKey(PrivateKey key, SecureString password) => Result.Success;
    public Result StoreKey(Address address, byte[] keyContent, SecureString password) => Result.Success;
    public Result DeleteKey(Address address) => Result.Success;
}
