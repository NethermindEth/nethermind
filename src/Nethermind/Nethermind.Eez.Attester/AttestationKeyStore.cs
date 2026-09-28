// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Security;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Eez.Attester;

/// <summary>The attestation key, kept in an encrypted Nethermind keystore and unlocked with a password file.</summary>
internal static class AttestationKeyStore
{
    /// <exception cref="InvalidOperationException">The key cannot be found or unlocked, or is not the attester's.</exception>
    public static PrivateKey Unlock(string directory, string passwordFile, Address attester, ILogManager logManager)
    {
        (PrivateKey? key, Result result) = Create(directory, logManager).GetKey(attester, FilePasswordProvider.GetPasswordFromFile(passwordFile));
        if (key is null)
        {
            throw new InvalidOperationException($"attestation key for {attester} cannot be unlocked: {result.Error}");
        }

        return key.Address == attester ? key : throw new InvalidOperationException("attestation key does not match the expected attester address");
    }

    /// <summary>Encrypts a raw private key into the keystore, for bringing up devnets from their fixed keys.</summary>
    public static Address Import(string directory, string passwordFile, string privateKeyHex, ILogManager logManager)
    {
        PrivateKey key = new(privateKeyHex.Trim());
        SecureString password = FilePasswordProvider.GetPasswordFromFile(passwordFile);
        Result result = Create(directory, logManager).StoreKey(key, password);
        return result.ResultType == ResultType.Success ? key.Address : throw new InvalidOperationException($"attestation key cannot be stored: {result.Error}");
    }

    private static FileKeyStore Create(string directory, ILogManager logManager)
    {
        KeyStoreConfig config = new() { KeyStoreDirectory = Path.GetFullPath(directory) };
        return new FileKeyStore(config, new EthereumJsonSerializer(), new AesEncrypter(config, logManager), new CryptoRandom(), logManager,
            new PrivateKeyStoreIOSettingsProvider(config));
    }
}
