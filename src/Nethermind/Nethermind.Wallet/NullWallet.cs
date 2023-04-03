// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Wallet
{
    public class NullWallet : IWallet
    {
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;

        public void Import(byte[] keyData, SecureString passphrase)
        {
        }

        private NullWallet()
        {
        }

        private static NullWallet _instance;

        public static NullWallet Instance => _instance ?? LazyInitializer.EnsureInitialized(ref _instance, () => new NullWallet());

        public Address NewAccount(SecureString passphrase)
        {
            throw new NotImplementedException();
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan)
        {
            AccountUnlocked?.Invoke(this, new AccountUnlockedEventArgs(address));
            return true;
        }

        public bool LockAccount(Address address)
        {
            AccountLocked?.Invoke(this, new AccountLockedEventArgs(address));
            return true;
        }

        public Signature Sign(Keccak message, Address address, SecureString passphrase)
        {
            return null;
        }

        public Address[] GetAccounts()
        {
            return Array.Empty<Address>();
        }

        public bool IsUnlocked(Address address)
        {
            return true;
        }

        public Signature Sign(Keccak message, Address address)
        {
            return null;
        }
    }
}
