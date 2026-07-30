// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.KeyStore;

namespace Nethermind.Wallet
{
    internal static class WalletSigner
    {
        public static Signature Sign(in ValueHash256 message, PrivateKey key)
        {
            byte[] rs = SecP256k1.SignCompact(message.Bytes, key.KeyBytes, out int v);
            return new Signature(rs, v);
        }

        // Decrypts the account with the passphrase for a single signing operation; the account is never unlocked.
        public static bool TrySignWithPassphrase(
            IKeyStore keyStore, in ValueHash256 message, Address address, SecureString passphrase, [NotNullWhen(true)] out Signature signature)
        {
            (PrivateKey key, Result result) = keyStore.GetKey(address, passphrase);
            if (result.ResultType != ResultType.Success)
            {
                signature = null;
                return false;
            }

            using PrivateKey disposableKey = key;
            signature = Sign(in message, disposableKey);
            return true;
        }
    }
}
