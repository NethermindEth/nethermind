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
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

[assembly: InternalsVisibleTo("Nethermind.Store.Test")]

namespace Nethermind.Store
{
    // TODO: separate CodeProvider out?
    public class StateProvider : IStateProvider
    {
        private const int StartCapacity = 8;

        private readonly LruCache<Address, Account> _longTermCache = new LruCache<Address, Account>(1024 * 1024); // ~100MB

        private readonly Dictionary<Address, Stack<int>> _intraBlockCache = new Dictionary<Address, Stack<int>>();

        private readonly HashSet<Address> _committedThisRound = new HashSet<Address>();

        private readonly List<Change> _keptInCache = new List<Change>();
        private readonly ILogger _logger;
        private readonly IDb _codeDb;

        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        public StateProvider(StateTree stateTree, ILogger logger, IDb codeDb)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _codeDb = codeDb;
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
            if (_intraBlockCache.ContainsKey(address))
            {
                return _changes[_intraBlockCache[address].Peek()].ChangeType != ChangeType.Delete;
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

        public void UpdateCodeHash(Address address, Keccak codeHash, IReleaseSpec releaseSpec)
        {
            Account account = GetThroughCache(address);
            if (account.CodeHash != codeHash)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  Update code hash of {address} to {codeHash}");
                }

                Account changedAccount = account.WithChangedCodeHash(codeHash);
                PushUpdate(address, changedAccount);
            }
            else if (releaseSpec.IsEip158Enabled)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  Touch {address} (code hash)");
                }

                Account touched = GetThroughCache(address);
                PushTouch(address, touched);
            }
        }

        public void UpdateBalance(Address address, BigInteger balanceChange, IReleaseSpec releaseSpec)
        {
            if (balanceChange == BigInteger.Zero)
            {
                if (releaseSpec.IsEip158Enabled)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Trace($"  Touch {address} (balance)");
                    }

                    Account touched = GetThroughCache(address);
                    PushTouch(address, touched);
                }

                return;
            }

            Account account = GetThroughCache(address);
            if (account == null)
            {
                _logger.Error("Updating balance of a non-existing account");
                throw new InvalidOperationException("Updating balance of a non-existing account");
            }

            BigInteger newbalance = account.Balance + balanceChange;
            if (newbalance < 0)
            {
                throw new InsufficientBalanceException();
            }

            Account changedAccount = account.WithChangedBalance(account.Balance + balanceChange);
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  Update {address} B = {account.Balance + balanceChange} B_CHANGE = {balanceChange}");
            }

            PushUpdate(address, changedAccount);
        }

        public void UpdateStorageRoot(Address address, Keccak storageRoot)
        {
            Account account = GetThroughCache(address);
            if (account.StorageRoot != storageRoot)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  Update {address} storage root = {storageRoot}");
                }

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
            _codeDb[codeHash.Bytes] = code;

            return codeHash;
        }

        public Keccak GetCodeHash(Address address)
        {
            Account account = GetThroughCache(address);
            return account?.CodeHash ?? Keccak.OfAnEmptyString;
        }

        public byte[] GetCode(Keccak codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString)
            {
                return new byte[0];
            }

            return _codeDb[codeHash.Bytes];
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
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  State snapshot {_currentPosition}");
            }

            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"{nameof(StateProvider)} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  Restoring state snapshot {snapshot}");
            }

            if (snapshot == _currentPosition)
            {
                return;
            }

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_intraBlockCache[change.Address].Count == 1)
                {
                    if (change.ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _intraBlockCache[change.Address].Pop();
                        if (actualPosition != _currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentPosition} - {i}");
                        }

                        _keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                _changes[_currentPosition - i] = null; // TODO: temp, ???
                int forChecking = _intraBlockCache[change.Address].Pop();
                if (forChecking != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forChecking} to be equal to {_currentPosition} - {i}");
                }

                if (_intraBlockCache[change.Address].Count == 0)
                {
                    _intraBlockCache.Remove(change.Address);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in _keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _intraBlockCache[kept.Address].Push(_currentPosition);
            }

            _keptInCache.Clear();
        }

        public void CreateAccount(Address address, BigInteger balance)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  CREATING ACCOUNT: {address} with balance {balance}");
            }

            Account account = new Account();
            account.Balance = balance;
            PushNew(address, account);
        }

        public void Commit(IReleaseSpec releaseSpec)
        {
            if (_currentPosition == -1)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug("  no state changes to commit");
                }

                return;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  committing state changes (at {_currentPosition})");
            }

            if (_changes[_currentPosition] == null)
            {
                throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(StateProvider)}");
            }

            if (_changes[_currentPosition + 1] != null)
            {
                throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(StateProvider)}");
            }

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_committedThisRound.Contains(change.Address))
                {
                    continue;
                }

                int forAssertion = _intraBlockCache[change.Address].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

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
                        if (releaseSpec.IsEip158Enabled && change.Account.IsEmpty)
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"  Remove empty {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            }

                            SetState(change.Address, null);
                        }
                        else
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"  Update {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            }

                            SetState(change.Address, change.Account);
                        }

                        break;
                    }
                    case ChangeType.New:
                    {
                        if (!releaseSpec.IsEip158Enabled || !change.Account.IsEmpty)
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"  Create {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            }

                            SetState(change.Address, change.Account);
                        }

                        break;
                    }
                    case ChangeType.Delete:
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"  Remove {change.Address}");
                        }

                        bool wasItCreatedNow = false;
                        while (_intraBlockCache[change.Address].Count > 0)
                        {
                            int previousOne = _intraBlockCache[change.Address].Pop();
                            wasItCreatedNow |= _changes[previousOne].ChangeType == ChangeType.New;
                            if (wasItCreatedNow)
                            {
                                break;
                            }
                        }

                        if (!wasItCreatedNow)
                        {
                            SetState(change.Address, null);
                        }

                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            _capacity = Math.Max(StartCapacity, _capacity / 2);
            _changes = new Change[_capacity];
            _currentPosition = -1;
            _committedThisRound.Clear();
            _intraBlockCache.Clear();
            _state.UpdateRootHash();
        }

        private Account GetState(Address address)
        {
            Account cached = _longTermCache.Get(address);
            if (cached != null)
            {
                return cached;
            }

            Metrics.StateTreeReads++;
            Account account = _state.Get(address);
            if (account == null)
            {
                return null;
            }

            _longTermCache.Set(address, account);
            return account;
        }

        private void SetState(Address address, Account account)
        {
            _longTermCache.Set(address, account);
            Metrics.StateTreeWrites++;
            _state.Set(address, account);
        }

        private Account GetAndAddToCache(Address address)
        {
            Account account = GetState(address);
            if (account != null)
            {
                PushJustCache(address, account);
            }

            return account;
        }

        private Account GetThroughCache(Address address)
        {
            if (_intraBlockCache.ContainsKey(address))
            {
                return _changes[_intraBlockCache[address].Peek()].Account;
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
            _intraBlockCache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(changeType, address, touchedAccount);
        }

        private void PushNew(Address address, Account account)
        {
            SetupCache(address);
            IncrementPosition();
            _intraBlockCache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.New, address, account);
        }

        private void IncrementPosition()
        {
            _currentPosition++;
            if (_currentPosition >= _capacity - 1) // sometimes we ask about the _currentPosition + 1;
            {
                _capacity *= 2;
                Array.Resize(ref _changes, _capacity);
            }
        }

        private void SetupCache(Address address)
        {
            if (!_intraBlockCache.ContainsKey(address))
            {
                _intraBlockCache[address] = new Stack<int>();
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

        public void Reset()
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("  CLEARING STATE PROVIDER CACHES");
            }

            _intraBlockCache.Clear();
            _currentPosition = -1;
            _committedThisRound.Clear();
            Array.Clear(_changes, 0, _changes.Length);
        }

        public void CommitTree()
        {
            _state.Commit();
        }
    }
}