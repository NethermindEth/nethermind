//  Copyright (c) 2020 Demerzel Solutions Limited
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using provide.Model.Vault;

namespace Nethermind.Vault
{
    public class VaultWallet : IVaultWallet
    {
        private ConcurrentDictionary<Address, Guid> _accounts;

        private readonly IVaultService _vaultService;

        private readonly Guid _vaultId;

        private readonly ILogger _logger;

        public VaultWallet(IVaultService vaultService, string vaultId, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _vaultService = vaultService;
            _vaultId = vaultId == null ? Guid.Empty : Guid.Parse(vaultId);
            _accounts = new ConcurrentDictionary<Address, Guid>();
        }

        public async Task<Address[]> GetAccounts()
        {
            IEnumerable<Key> keys = await _vaultService.ListKeys(_vaultId);
            _accounts = new ConcurrentDictionary<Address, Guid>(
                keys.Where(k => k.Id != null).Select(ToKeyValuePair));

            if (_logger.IsTrace)
            {
                foreach (var key in keys)
                {
                    _logger.Trace($"Retrieved key {key.Address} {key.Id} from vault {key.VaultId} (intended vault {_vaultId})");
                }
            }

            return _accounts.Keys.ToArray();
        }
        
        public async Task<Address> CreateAccount()
        {
            Key key = new Key();
            key.Name = "name";
            key.Description = "description";
            key.Type = "asymmetric";
            key.Spec = "secp256k1";
            key.Usage = "sign/verify";

            Key createdKey = await _vaultService.CreateKey(_vaultId, key);
            if (createdKey is null)
            {
                throw new ApplicationException("Failed to create vault key.");
            }

            if (createdKey.Id is null)
            {
                throw new ApplicationException($"Failed to create vault key with a valid {nameof(key.Id)}.");
            }

            if(_logger.IsTrace) _logger.Trace(
                $"Created key {createdKey.Address} {createdKey.Id} in vault {createdKey.VaultId}");
            
            Address account = new Address(createdKey.Address);
            if (!_accounts.TryAdd(account, createdKey.Id.Value))
            {
                throw new ApplicationException("New key created with an address collision.");
            }

            return account;
        }

        public async Task DeleteAccount(Address address)
        {
            Guid? keyId = await RetrieveId(address);
            if (keyId is null)
            {
                throw new KeyNotFoundException($"Account with the given address {address} could not be found");
            }
            
            if(_logger.IsTrace) _logger.Trace($"Deleting key {keyId} from vault {_vaultId}");
            await _vaultService.DeleteKey(_vaultId, keyId.Value);
        }

        public async Task<Signature> Sign(Address address, Keccak message)
        {
            Guid? keyId = await RetrieveId(address);
            if (keyId is null)
            {
                throw new KeyNotFoundException($"Account with the given address {address} could not be found");
            }
            
            string signature = await _vaultService.Sign(_vaultId, keyId.Value, message.ToString(false));
            return new Signature(signature);
        }

        public async Task<bool> Verify(Address address, Keccak message, Signature signature)
        {
            if (address is null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            if (signature is null)
            {
                throw new ArgumentNullException(nameof(signature));
            }

            Guid? keyId = await RetrieveId(address);
            if (keyId is null)
            {
                throw new KeyNotFoundException($"Account with the given address {address} could not be found");
            }
            
            string sig = Convert.ToString(signature)!.Remove(0, 2);
            bool result = await _vaultService.Verify(_vaultId, keyId.Value, message.ToString(false), sig);
            return result;
        }

        public async Task<Guid?> RetrieveId(Address address)
        {
            await GetAccounts();
            if (!_accounts.ContainsKey(address))
            {
                return null;
            }
            
            return _accounts[address];
        }

        private static KeyValuePair<Address, Guid> ToKeyValuePair(Key key)
        {
            if (key.Id == null)
            {
                throw new ArgumentException($"Can only convert keys with {nameof(key.Id)} that is not NULL");
            }
            
            if (key.Address == null)
            {
                throw new ArgumentException($"Can only convert keys with {nameof(key.Address)} that is not NULL ({key.Name})");
            }

            Address address = new Address(key.Address);
            Guid id = key.Id.Value;
            return new KeyValuePair<Address, Guid>(address, id);
        }
    }
}
