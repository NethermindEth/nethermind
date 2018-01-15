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
using System.Diagnostics;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Potocol;

namespace Nevermind.Store
{
    public class StateProvider : IStateProvider
    {
        private const int StartCapacity = 1024;

        private readonly Dictionary<Address, Stack<int>> _cache = new Dictionary<Address, Stack<int>>();
        private readonly Dictionary<Keccak, byte[]> _code = new Dictionary<Keccak, byte[]>();

        private readonly HashSet<Address> _committedThisRound = new HashSet<Address>();

        private readonly List<Change> _keptInCache = new List<Change>();
        public IEthereumRelease EthereumRelease { get; set; }
        private readonly ILogger _logger;

        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        public StateProvider(StateTree stateTree, IEthereumRelease ethereumRelease, ILogger logger)
        {
            EthereumRelease = ethereumRelease;
            _logger = logger;
            _state = stateTree;
        }
        
        public Keccak StateRoot
        {
            get => _state.RootHash;
            set => _state.RootHash = value;
        }

        private readonly StateTree _state;

        public bool AccountExists(Address address)
        {
            if (_cache.ContainsKey(address))
            {
                return _changes[_cache[address].Peek()].ChangeType != ChangeType.Delete;
            }

            return GetAndAddToCache(address) != null;
        }

        public bool IsEmptyAccount(Address address)
        {
            return GetThroughCache(address).IsEmpty;
        }

        public bool IsDeadAccount(Address address)
        {
            Account account = GetThroughCache(address);
            return account?.IsEmpty ?? true;
        }

        public BigInteger GetNonce(Address address)
        {
            Account account = GetThroughCache(address);
            return account?.Nonce ?? BigInteger.Zero;
        }
        
        public Keccak GetStorageRoot(Address address)
        {
            Account account = GetThroughCache(address);
            return account.StorageRoot;
        }

        public BigInteger GetBalance(Address address)
        {
            Account account = GetThroughCache(address);
            return account?.Balance ?? BigInteger.Zero;
        }

        public void UpdateCodeHash(Address address, Keccak codeHash)
        {
            Account account = GetThroughCache(address);
            if (account.CodeHash != codeHash)
            {
                _logger?.Log($"  UPDATE CODE HASH of {address} to {codeHash}");
                Account changedAccount = account.WithChangedCodeHash(codeHash);
                PushUpdate(address, changedAccount);
            }
            else if (EthereumRelease.IsEip158Enabled)
            {
                _logger?.Log($"  TOUCH {address} (code hash)");
                Account touched = GetThroughCache(address);
                PushTouch(address, touched);
            }
        }

        public void UpdateBalance(Address address, BigInteger balanceChange)
        {
            if (balanceChange == BigInteger.Zero)
            {
                if (EthereumRelease.IsEip158Enabled)
                {
                    _logger?.Log($"  TOUCH {address} (balance)");
                    Account touched = GetThroughCache(address);
                    PushTouch(address, touched);
                }

                return;
            }

            Account account = GetThroughCache(address);

            BigInteger newbalance = account.Balance + balanceChange;
            if (newbalance < 0)
            {
                throw new InsufficientBalanceException();
            }

            Account changedAccount = account.WithChangedBalance(account.Balance + balanceChange);
            _logger?.Log($"  UPDATE {address} B = {account.Balance + balanceChange} B_CHANGE = {balanceChange}");

            PushUpdate(address, changedAccount);
        }

        public void UpdateStorageRoot(Address address, Keccak storageRoot)
        {
            Account account = GetThroughCache(address);
            if (account.StorageRoot != storageRoot)
            {
                _logger?.Log($"  UPDATE {address} STORAGE ROOT = {storageRoot}");
                Account changedAccount = account.WithChangedStorageRoot(storageRoot);
                PushUpdate(address, changedAccount);
            }
        }

        public void IncrementNonce(Address address)
        {
            //if (ShouldLog.State) Console.WriteLine($"  SETTING NONCE of {address}");

            Account account = GetThroughCache(address);
            Account changedAccount = account.WithChangedNonce(account.Nonce + 1);
            PushUpdate(address, changedAccount);
        }

        public Keccak UpdateCode(byte[] code)
        {
            if (code.Length == 0)
            {
                return Keccak.OfAnEmptyString;
            }

            Keccak codeHash = Keccak.Compute(code);
            _code[codeHash] = code;

            return codeHash;
        }

        public byte[] GetCode(Keccak codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString)
            {
                return new byte[0];
            }

            return _code[codeHash];
        }

        public byte[] GetCode(Address address)
        {
            Account account = GetThroughCache(address);
            if (account == null)
            {
                return new byte[0];
            }

            return GetCode(account.CodeHash);
        }

        public void DeleteAccount(Address address)
        {
            PushDelete(address);
        }

        public int TakeSnapshot()
        {
            _logger?.Log($"  STATE SNAPSHOT {_currentPosition}");
            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            Debug.Assert(snapshot <= _currentPosition, "INVALID SNAPSHOT");
            _logger?.Log($"  RESTORING STATE SNAPSHOT {snapshot}");

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_cache[change.Address].Count == 1)
                {
                    if (change.ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _cache[change.Address].Pop();
                        Debug.Assert(_currentPosition - i == actualPosition);
                        _keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                _changes[_currentPosition - i] = null; // TODO: temp
                int forAssertion = _cache[change.Address].Pop();
                Debug.Assert(forAssertion == _currentPosition - i);

                if (_cache[change.Address].Count == 0)
                {
                    _cache.Remove(change.Address);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in _keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _cache[kept.Address].Push(_currentPosition);
            }

            _keptInCache.Clear();
        }

        public void CreateAccount(Address address, BigInteger balance)
        {
            _logger?.Log($"  CREATING ACCOUNT: {address} with balance {balance}");

            Account account = new Account();
            account.Balance = balance;
            PushNew(address, account);
        }

        public void Commit()
        {
            _logger?.Log("  COMMITTING STATE CHANGES");

            if (_currentPosition == -1)
            {
                return;
            }

            Debug.Assert(_changes[_currentPosition] != null);
            Debug.Assert(_changes[_currentPosition + 1] == null);

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_committedThisRound.Contains(change.Address))
                {
                    continue;
                }

                int forAssertion = _cache[change.Address].Pop();
                Debug.Assert(forAssertion == _currentPosition - i);

                _committedThisRound.Add(change.Address);

                switch (change.ChangeType)
                {
                    case ChangeType.JustCache:
                    {
                        break;
                    }
                    case ChangeType.Touch:
                    case ChangeType.Update:
                    {
                        if (EthereumRelease.IsEip158Enabled && change.Account.IsEmpty)
                        {
                            _logger?.Log($"  DELETE EMPTY {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            _state.Set(change.Address, null);
                        }
                        else
                        {
                            _logger?.Log($"  UPDATE {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            _state.Set(change.Address, Rlp.Encode(change.Account));
                        }

                        break;
                    }
                    case ChangeType.New:
                    {
                        _logger?.Log($"  CREATE {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");

                        if (!EthereumRelease.IsEip158Enabled || !change.Account.IsEmpty)
                        {
                            _state.Set(change.Address, Rlp.Encode(change.Account));
                        }

                        break;
                    }
                    case ChangeType.Delete:
                    {
                        _logger?.Log($"  DELETE {change.Address}");

                        bool wasItCreatedNow = false;
                        while (_cache[change.Address].Count > 0)
                        {
                            int previousOne = _cache[change.Address].Pop();
                            wasItCreatedNow |= _changes[previousOne].ChangeType == ChangeType.New;
                            if (wasItCreatedNow)
                            {
                                break;
                            }
                        }

                        if (!wasItCreatedNow)
                        {
                            _state.Set(change.Address, null);
                        }
                        break;
                    }
                    default:
                    throw new ArgumentOutOfRangeException();
                }
            }

            _capacity = 1024;
            _changes = new Change[_capacity];
            _currentPosition = -1;
            _committedThisRound.Clear();
            _cache.Clear();
        }

        private Account GetAccount(Address address)
        {
            Rlp rlp = _state.Get(address);
            if (rlp.Bytes == null)
            {
                return null;
            }

            return Rlp.Decode<Account>(rlp);
        }

        private Account GetAndAddToCache(Address address)
        {
            Account account = GetAccount(address);
            if (account != null)
            {
                PushJustCache(address, account);
            }

            return account;
        }

        private Account GetThroughCache(Address address)
        {
            if (_cache.ContainsKey(address))
            {
                return _changes[_cache[address].Peek()].Account;
            }

            Account account = GetAndAddToCache(address);
            return account;
        }

        private void PushJustCache(Address address, Account account)
        {
            Push(ChangeType.JustCache, address, account);
        }

        private void PushUpdate(Address address, Account account)
        {
            Push(ChangeType.Update, address, account);
        }

        private void PushTouch(Address address, Account account)
        {
            Push(ChangeType.Touch, address, account);
        }

        private void PushDelete(Address address)
        {
            Push(ChangeType.Delete, address, null);
        }

        private void Push(ChangeType changeType, Address address, Account touchedAccount)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(changeType, address, touchedAccount);
        }

        private void PushNew(Address address, Account account)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.New, address, account);
        }

        private void IncrementPosition()
        {
            _currentPosition++;
            if (_currentPosition > _capacity - 1)
            {
                _capacity *= 2;
                Array.Resize(ref _changes, _capacity);
            }
        }

        private void SetupCache(Address address)
        {
            if (!_cache.ContainsKey(address))
            {
                _cache[address] = new Stack<int>();
            }
        }

        private enum ChangeType
        {
            JustCache,
            Touch,
            Update,
            New,
            Delete
        }

        private class Change
        {
            public Change(ChangeType type, Address address, Account account)
            {
                ChangeType = type;
                Address = address;
                Account = account;
            }

            public ChangeType ChangeType { get; }
            public Address Address { get; }
            public Account Account { get; }
        }
        
        public void ClearCaches()
        {
            _logger?.Log("  CLEARING STATE PROVIDER CACHES");
            
            _cache.Clear();
            _currentPosition = -1;
            _committedThisRound.Clear();
            Array.Clear(_changes, 0, _changes.Length);
        }
    }
}