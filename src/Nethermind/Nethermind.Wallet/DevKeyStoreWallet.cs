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
using Nethermind.Core.Encoding;
using Nethermind.Core.Model;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Secp256k1;

namespace Nethermind.Wallet
{
    [DoNotUseInSecuredContext("For dev purposes only")]
    public class DevKeyStoreWallet : IWallet
    {
        private static readonly byte[] KeySeed = new byte[32];
        private readonly IKeyStore _keyStore;
        private readonly ILogger _logger;

        private readonly Dictionary<Address, PrivateKey> _unlockedAccounts = new Dictionary<Address, PrivateKey>();
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;

        public DevKeyStoreWallet(IKeyStore keyStore, ILogManager logManager)
        {
            _keyStore = keyStore;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            KeySeed[31] = 1;
            for (int i = 0; i < 3; i++)
            {
                PrivateKey key = new PrivateKey(KeySeed);
                if (GetAccounts().All(a => a != key.Address))
                {
                    SecureString secureString = new SecureString();
                    secureString.MakeReadOnly();
                    _keyStore.StoreKey(key, secureString);
                }

                _unlockedAccounts.Add(key.Address, key);
                KeySeed[31]++;
            }
        }

        public void Import(byte[] keyData, SecureString passphrase)
        {
            throw new NotSupportedException();
        }

        public Address[] GetAccounts()
        {
            return _keyStore.GetKeyAddresses().Addresses.ToArray();
        }

        public Address NewAccount(SecureString passphrase)
        {
            (PrivateKey privateKey, _) = _keyStore.GenerateKey(passphrase);
            return privateKey.Address;
        }

        public bool UnlockAccount(Address address, SecureString passphrase)
        {
            return UnlockAccount(address, passphrase, TimeSpan.FromSeconds(300));
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan timeSpan)
        {
            if (address == Address.Zero)
            {
                return false;
            }
            
            if (_unlockedAccounts.ContainsKey(address)) return true;

            (PrivateKey key, Result result) = _keyStore.GetKey(address, passphrase);
            if (result.ResultType == ResultType.Success)
            {
                if (_logger.IsInfo) _logger.Info($"Unlocking account: {address}");
                _unlockedAccounts.Add(key.Address, key);
                AccountUnlocked?.Invoke(this, new AccountUnlockedEventArgs(address));
                return true;
            }

            if (_logger.IsError) _logger.Error($"Failed to unlock the account: {address}");
            return false;
        }

        public bool LockAccount(Address address)
        {
            AccountLocked?.Invoke(this, new AccountLockedEventArgs(address));
            return _unlockedAccounts.Remove(address);
        }

        public void Sign(Transaction tx, int chainId)
        {
            if (_logger.IsDebug) _logger?.Debug($"Signing transaction: {tx.Value} to {tx.To}");
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, true, chainId));
            tx.Signature = Sign(hash, tx.SenderAddress);
            tx.Signature.V = tx.Signature.V + 8 + 2 * chainId;
        }

        public bool IsUnlocked(Address address) => _unlockedAccounts.ContainsKey(address);
        
        public Signature Sign(Keccak message, Address address, SecureString passphrase)
        {
            PrivateKey key;
            if (_unlockedAccounts.ContainsKey(address))
            {
                key = _unlockedAccounts[address];
            }
            else
            {
                if (passphrase == null) throw new SecurityException("Passphrase missing when trying to sign a message");

                key = _keyStore.GetKey(address, passphrase).PrivateKey;
            }

            var rs = Proxy.SignCompact(message.Bytes, key.KeyBytes, out int v);
            return new Signature(rs, v);
        }
        
        public Signature Sign(Keccak message, Address address)
        {
            PrivateKey key;
            if (_unlockedAccounts.ContainsKey(address))
            {
                key = _unlockedAccounts[address];
            }
            else
            {
                throw new SecurityException("can only sign without passphrase when account is unlocked.");
            }

            var rs = Proxy.SignCompact(message.Bytes, key.KeyBytes, out int v);
            return new Signature(rs, v);
        }
    }
}