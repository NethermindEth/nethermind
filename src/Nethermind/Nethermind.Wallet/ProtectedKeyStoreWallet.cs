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
using System.Linq;
using System.Runtime.Caching;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Secp256k1;

namespace Nethermind.Wallet
{
    public class ProtectedKeyStoreWallet : IWallet
    {
        private static readonly TimeSpan DefaultExpirationTime = TimeSpan.FromMinutes(5);
        
        private readonly IKeyStore _keyStore;
        private readonly IProtectedPrivateKeyFactory _protectedPrivateKeyFactory;
        private readonly ITimestamper _timestamper;
        private readonly ILogger _logger;

        private readonly MemoryCache _unlockedAccounts;
        public event EventHandler<AccountLockedEventArgs> AccountLocked;
        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked;

        public ProtectedKeyStoreWallet(IKeyStore keyStore, IProtectedPrivateKeyFactory protectedPrivateKeyFactory, ITimestamper timestamper, ILogManager logManager)
        {
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            _protectedPrivateKeyFactory = protectedPrivateKeyFactory ?? throw new ArgumentNullException(nameof(protectedPrivateKeyFactory));
            _timestamper = timestamper ?? Timestamper.Default;
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _unlockedAccounts = new MemoryCache(nameof(ProtectedKeyStoreWallet));
        }

        public void Import(byte[] keyData, SecureString passphrase)
        {
            _keyStore.StoreKey(new PrivateKey(keyData), passphrase);
        }

        public Address[] GetAccounts() => _keyStore.GetKeyAddresses().Addresses.ToArray();

        public Address NewAccount(SecureString passphrase)
        {
            (PrivateKey privateKey, _) = _keyStore.GenerateKey(passphrase);
            return privateKey.Address;
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan)
        {
            if (address == Address.Zero)
            {
                return false;
            }
            else if (IsUnlocked(address))
            {
                return true;
            }
            else
            {
                (PrivateKey key, Result result) = _keyStore.GetKey(address, passphrase);
                if (result.ResultType == ResultType.Success)
                {
                    if (_logger.IsInfo) _logger.Info($"Unlocking account: {address}");
                    _unlockedAccounts.Add(key.Address.ToString(), _protectedPrivateKeyFactory.Create(key),
                        new CacheItemPolicy() {Priority = CacheItemPriority.NotRemovable, AbsoluteExpiration = _timestamper.UtcNowOffset + (timeSpan ?? DefaultExpirationTime)});
                    AccountUnlocked?.Invoke(this, new AccountUnlockedEventArgs(address));
                    return true;
                }

                if (_logger.IsError) _logger.Error($"Failed to unlock the account: {address}");
                return false;
            }
        }

        public bool LockAccount(Address address)
        {
            AccountLocked?.Invoke(this, new AccountLockedEventArgs(address));
            _unlockedAccounts.Remove(address.ToString());
            return true;
        }
        
        public bool IsUnlocked(Address address) => _unlockedAccounts.Contains(address.ToString());

        public Signature Sign(Keccak message, Address address, SecureString passphrase)
            => SignCore(message, address, () =>
            {
                if (passphrase == null) throw new SecurityException("Passphrase missing when trying to sign a message");
                return _keyStore.GetKey(address, passphrase).PrivateKey;
            });
        
        public Signature Sign(Keccak message, Address address) => SignCore(message, address, () => throw new SecurityException("Can only sign without passphrase when account is unlocked."));

        private Signature SignCore(Keccak message, Address address, Func<PrivateKey> getPrivateKeyWhenNotFound)
        {
            var protectedPrivateKey = (ProtectedPrivateKey) _unlockedAccounts.Get(address.ToString());
            using PrivateKey key = protectedPrivateKey != null ? protectedPrivateKey.Unprotect() : getPrivateKeyWhenNotFound();
            var rs = Proxy.SignCompact(message.Bytes, key.KeyBytes, out int v);
            return new Signature(rs, v);
        }
    }
}
