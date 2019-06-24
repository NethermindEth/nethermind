/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Security;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Wallet
{
    public class NullWallet : IWallet
    {
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;
        
        public void Import(byte[] keyData, SecureString passphrase)
        {
        }

        private static NullWallet _instance;
        
        public static NullWallet Instance => _instance ?? LazyInitializer.EnsureInitialized(ref _instance, () => new NullWallet());

        public Address NewAccount(SecureString passphrase)
        {
            throw new NotImplementedException();
        }

        public bool UnlockAccount(Address address, SecureString passphrase)
        {
            return true;
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan timeSpan)
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
            throw new NotImplementedException();
        }

        public Address[] GetAccounts()
        {
            return Array.Empty<Address>();
        }

        public void Sign(Transaction tx, int chainId)
        {
            throw new NotImplementedException();
        }

        public bool IsUnlocked(Address address)
        {
            return true;
        }

        public Signature Sign(Keccak message, Address address)
        {
            throw new NotImplementedException();
        }
    }
}