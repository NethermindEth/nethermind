// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Wallet
{
    public interface IWallet
    {
        void Import(byte[] keyData, SecureString passphrase);
        Address NewAccount(SecureString passphrase);
        bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan = null);
        bool LockAccount(Address address);
        bool IsUnlocked(Address address);
        Signature Sign(Hash256 message, Address address, SecureString passphrase = null);
        Signature Sign(Hash256 message, Address address);
        Address[] GetAccounts();
        void Sign(Transaction tx, ulong chainId)
        {
            Hash256 hash = Keccak.Compute(Rlp.Encode(tx, true, true, chainId).Bytes);
            tx.Signature = Sign(hash, tx.SenderAddress);
            if (tx.Signature is null)
            {
                throw new CryptographicException($"Failed to sign tx {tx.Hash} using the {tx.SenderAddress} address.");
            }

            tx.Signature.V = tx.Type == TxType.Legacy ? tx.Signature.V + 8 + 2 * chainId : (ulong)(tx.Signature.RecoveryId + 27);
        }
        Signature SignMessage(byte[] message, Address address)
        {
            string m = Encoding.UTF8.GetString(message);
            string signatureText = $"\u0019Ethereum Signed Message:\n{m.Length}{m}";
            return Sign(Keccak.Compute(signatureText), address);
        }
        event EventHandler<AccountLockedEventArgs> AccountLocked;
        event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;
    }
}
