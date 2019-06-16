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
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Wallet
{
    public interface IWallet : IBasicWallet
    {
        void Import(byte[] keyData, SecureString passphrase);
        Address NewAccount(SecureString passphrase);
        bool UnlockAccount(Address address, SecureString passphrase);
        bool UnlockAccount(Address address, SecureString passphrase, TimeSpan timeSpan);
        bool LockAccount(Address address);
        Signature Sign(Keccak message, Address address, SecureString passphrase = null);
        Address[] GetAccounts();
        void Sign(Transaction tx, int chainId);
        bool IsUnlocked(Address address);
        event EventHandler<AccountLockedEventArgs> AccountLocked;
        event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;
    }
}