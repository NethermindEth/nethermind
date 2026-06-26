// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        Address[] GetAccounts();
        event EventHandler<AccountLockedEventArgs>? AccountLocked;
        event EventHandler<AccountUnlockedEventArgs>? AccountUnlocked;

        bool TrySign(in ValueHash256 message, Address address, [NotNullWhen(true)] out Signature? signature);

        bool TrySignMessage(byte[] message, Address address, [NotNullWhen(true)] out Signature? signature)
        {
            ValueHash256 hash = Eip191Hasher.HashMessageValue(message);
            return TrySign(in hash, address, out signature);
        }

        bool TrySign(in ValueHash256 message, Address address, SecureString passphrase, [NotNullWhen(true)] out Signature? signature)
            => TrySign(in message, address, out signature);

        bool TrySignMessage(byte[] message, Address address, SecureString passphrase, [NotNullWhen(true)] out Signature? signature)
        {
            ValueHash256 hash = Eip191Hasher.HashMessageValue(message);
            return TrySign(in hash, address, passphrase, out signature);
        }

        bool TrySignTransaction(Transaction tx, ulong chainId)
        {
            ValueHash256 hash = ValueKeccak.Compute(Rlp.Encode(tx, true, true, chainId).Bytes);
            Address? senderAddress = tx.SenderAddress;
            if (senderAddress is null || !TrySign(in hash, senderAddress, out Signature? sig)) return false;
            sig.V = tx.Type == TxType.Legacy ? sig.V + 8 + 2 * chainId : (ulong)(sig.RecoveryId + 27);
            tx.Signature = sig;
            return true;
        }
    }
}
