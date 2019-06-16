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
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Wallet
{
    public class HiveWallet : IWallet
    {
        private readonly ISet<Address> _addresses = new HashSet<Address>();
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;
        
        public void Add(Address address) => _addresses.Add(address);

        public void Import(byte[] keyData, SecureString passphrase)
        {
            throw new System.NotSupportedException();
        }

        public Address NewAccount(SecureString passphrase)
        {
            throw new System.NotSupportedException();
        }

        public bool UnlockAccount(Address address, SecureString passphrase)
        {
            throw new System.NotSupportedException();
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan timeSpan)
        {
            AccountUnlocked?.Invoke(this, new AccountUnlockedEventArgs(address));
            throw new NotSupportedException();
        }

        public bool LockAccount(Address address)
        {
            AccountLocked?.Invoke(this, new AccountLockedEventArgs(address));
            throw new NotSupportedException();
        }

        public Signature Sign(byte[] message, Address address, SecureString passphrase = null)
        {
            throw new NotSupportedException();
        }

        public Signature Sign(Keccak message, Address address, SecureString passphrase)
        {
            throw new NotSupportedException();
        }

        public Address[] GetAccounts() => _addresses.ToArray();

        public void Sign(Transaction tx, int chainId)
        {
            throw new NotSupportedException();
        }

        public bool IsUnlocked(Address address)
        {
            throw new NotImplementedException();
        }

        public Signature Sign(Keccak message, Address address)
        {
            throw new NotImplementedException();
        }     
    }
}