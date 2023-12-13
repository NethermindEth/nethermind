// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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
        event EventHandler<AccountLockedEventArgs> AccountLocked;
        event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;
    }
}
