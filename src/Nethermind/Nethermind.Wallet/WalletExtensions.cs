// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Wallet
{
    public static class WalletExtensions
    {
        public static void SetupTestAccounts(this IWallet wallet, byte count)
        {
            byte[] keySeed = new byte[32];
            keySeed[31] = 1;
            for (int i = 0; i < count; i++)
            {
                PrivateKey key = new PrivateKey(keySeed);
                SecureString secureString = string.Empty.Secure();
                if (wallet.GetAccounts().All(a => a != key.Address))
                {
                    wallet.Import(keySeed, secureString);
                }

                wallet.UnlockAccount(key.Address, secureString, TimeSpan.FromHours(24));
                keySeed[31]++;
            }
        }

        public static void Sign(this IWallet @this, Transaction tx, ulong chainId)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, true, chainId).Bytes);
            tx.Signature = @this.Sign(hash, tx.SenderAddress);
            if (tx.Signature is null)
            {
                throw new CryptographicException($"Failed to sign tx {tx.Hash} using the {tx.SenderAddress} address.");
            }

            tx.Signature.V = tx.Type == TxType.Legacy ? tx.Signature.V + 8 + 2 * chainId : (ulong)(tx.Signature.RecoveryId + 27);
        }
    }
}
