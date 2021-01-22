//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Secp256k1;

namespace Nethermind.Wallet
{   
    [DoNotUseInSecuredContext("For dev purposes only")]
    public class DevWallet : IWallet
    {
        private const string AnyPassword = "#DEV_ACCOUNT_NETHERMIND_ANY_PASSWORD#";
        private static byte[] _keySeed = new byte[32];
        private readonly ILogger _logger;
        private Dictionary<Address, bool> _isUnlocked = new Dictionary<Address, bool>();
        private Dictionary<Address, PrivateKey> _keys = new Dictionary<Address, PrivateKey>();
        private Dictionary<Address, string> _passwords = new Dictionary<Address, string>();
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;

        public DevWallet(IWalletConfig walletConfig, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _keySeed[31] = 1;
            for (int i = 0; i < walletConfig?.DevAccounts; i++)
            {
                PrivateKey key = new PrivateKey(_keySeed);
                _keys.Add(key.Address, key);
                _passwords.Add(key.Address, AnyPassword);
                _isUnlocked.Add(key.Address, true);
                _keySeed[31]++;
            }
        } 

        public void Import(byte[] keyData, SecureString passphrase)
        {
            throw new NotSupportedException();
        }

        public Address[] GetAccounts()
        {
            return _keys.Keys.ToArray();
        }

        public Address NewAccount(SecureString passphrase)
        {
            using var privateKeyGenerator = new PrivateKeyGenerator();
            PrivateKey key = privateKeyGenerator.Generate();
            _keys.Add(key.Address, key);
            _isUnlocked.Add(key.Address, true);
            _passwords.Add(key.Address, passphrase.Unsecure());
            return key.Address;
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan)
        {
            
            if (address is null || address == Address.Zero)
            {
                return false;
            }
            
            if (!_passwords.ContainsKey(address))
            {
                if (_logger.IsError) _logger.Error("Account does not exist.");
                return false;
            }

            if (!CheckPassword(address, passphrase))
            {
                if (_logger.IsError) _logger.Error("Password is invalid.");
                return false;
            }

            AccountUnlocked?.Invoke(this, new AccountUnlockedEventArgs(address));
            _isUnlocked[address] = true;
            
            return true;
        }

        public bool LockAccount(Address address)
        {
            if (!_passwords.ContainsKey(address))
            {
                if (_logger.IsError) _logger.Error("Account does not exist.");
                return false;
            }

            AccountLocked?.Invoke(this, new AccountLockedEventArgs(address));
            _isUnlocked[address] = false;

            return true;
        }
        public Signature Sign(Keccak message, Address address, SecureString passphrase)
        {
            if (!_isUnlocked.ContainsKey(address)) throw new SecurityException("Account does not exist.");

            if (!_isUnlocked[address] && !CheckPassword(address, passphrase)) throw new SecurityException("Cannot sign without password or unlocked account.");

            return Sign(message, address);
        }

        public bool IsUnlocked(Address address) => _isUnlocked.TryGetValue(address, out var unlocked) && unlocked;

        private bool CheckPassword(Address address, SecureString passphrase)
        {
            return _passwords[address] == AnyPassword || passphrase?.Unsecure() == _passwords[address];
        }

        public Signature Sign(Keccak message, Address address)
        {
            var rs = Proxy.SignCompact(message.Bytes, _keys[address].KeyBytes, out int v);
            return new Signature(rs, v);
        }
    }
}
