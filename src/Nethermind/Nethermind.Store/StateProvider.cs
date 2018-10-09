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
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;

[assembly: InternalsVisibleTo("Nethermind.Store.Test")]

namespace Nethermind.Store
{
    public class StateProvider : IStateProvider
    {
        private const int StartCapacity = 8;

        private readonly Dictionary<Address, Stack<int>> _intraBlockCache = new Dictionary<Address, Stack<int>>();

        private readonly HashSet<Address> _committedThisRound = new HashSet<Address>();

        private readonly List<Change> _keptInCache = new List<Change>();
        private readonly ILogger _logger;
        private readonly IDb _codeDb;

        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        public StateProvider(StateTree stateTree, IDb codeDb, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _state = stateTree ?? throw new ArgumentNullException(nameof(stateTree));
        }

        public Keccak StateRoot
        {
            get
            {
                _state.UpdateRootHash();
                return _state.RootHash;
            }
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

        public UInt256 GetNonce(Address address)
        {
            Account account = GetThroughCache(address);
            return account?.Nonce ?? UInt256.Zero;
        }

        public Keccak GetStorageRoot(Address address)
        {
            Account account = GetThroughCache(address);
            return account.StorageRoot;
        }

        public UInt256 GetBalance(Address address)
        {
            Account account = GetThroughCache(address);
            return account?.Balance ?? UInt256.Zero;
        }

        public void UpdateCodeHash(Address address, Keccak codeHash, IReleaseSpec releaseSpec)
        {
            Account account = GetThroughCache(address);
            if (account.CodeHash != codeHash)
            {
                if (_logger.IsTrace) _logger.Trace($"  Update code hash of {address} to {codeHash}");
                Account changedAccount = account.WithChangedCodeHash(codeHash);
                PushUpdate(address, changedAccount);
            }
            else if (releaseSpec.IsEip158Enabled)
            {
                if (_logger.IsTrace) _logger.Trace($"  Touch {address} (code hash)");
                Account touched = GetThroughCache(address);
                PushTouch(address, touched);
            }
        }

        private void SetNewBalance(Address address, UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSubtracting)
        {
            if (balanceChange.IsZero)
            {
                if (releaseSpec.IsEip158Enabled)
                {
                    if (_logger.IsTrace) _logger.Trace($"  Touch {address} (balance)");
                    Account touched = GetThroughCache(address);
                    PushTouch(address, touched);
                }

                return;
            }

            Account account = GetThroughCache(address);
            if (account == null)
            {
                if (_logger.IsError) _logger.Error("Updating balance of a non-existing account");
                throw new InvalidOperationException("Updating balance of a non-existing account");
            }

            if (isSubtracting && account.Balance < balanceChange)
            {
                throw new InsufficientBalanceException();
            }

            UInt256 newBalance = isSubtracting ? account.Balance - balanceChange : account.Balance + balanceChange;

            Account changedAccount = account.WithChangedBalance(newBalance);
            if (_logger.IsTrace) _logger.Trace($"  Update {address} B = {newBalance} B_CHANGE = {(isSubtracting ? "-" : "+")}{balanceChange}");
            PushUpdate(address, changedAccount);
        }

        public void SubtractFromBalance(Address address, UInt256 balanceChange, IReleaseSpec releaseSpec)
        {
            SetNewBalance(address, balanceChange, releaseSpec, true);
        }

        public void AddToBalance(Address address, UInt256 balanceChange, IReleaseSpec releaseSpec)
        {
            SetNewBalance(address, balanceChange, releaseSpec, false);
        }

        /// <summary>
        /// This is a coupling point between storage provider and state provider.
        /// This is pointing at the architectural change likely required where Storage and State Provider are represented by a single world state class.
        /// </summary>
        /// <param name="address"></param>
        /// <param name="storageRoot"></param>
        public void UpdateStorageRoot(Address address, Keccak storageRoot)
        {
            Account account = GetThroughCache(address);
            if (account.StorageRoot != storageRoot)
            {
                if (_logger.IsTrace) _logger.Trace($"  Update {address} storage root = {storageRoot}");
                Account changedAccount = account.WithChangedStorageRoot(storageRoot);
                PushUpdate(address, changedAccount);
            }
        }

        public void IncrementNonce(Address address)
        {
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
            if (_logger.IsTrace) _logger.Trace($"State snapshot {_currentPosition}");
            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"{nameof(StateProvider)} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
            }

            if (_logger.IsTrace) _logger.Trace($"Restoring state snapshot {snapshot}");
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

        public void CreateAccount(Address address, UInt256 balance)
        {
            if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance}");
            Account account = balance.IsZero ? Account.TotallyEmpty : new Account(balance);
            PushNew(address, account);
        }

        public void Commit(IReleaseSpec releaseSpec)
        {
            if (_currentPosition == -1)
            {
                if (_logger.IsTrace) _logger.Trace("  no state changes to commit");
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"  committing state changes (at {_currentPosition})");
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
                            if (_logger.IsTrace) _logger.Trace($"  Remove empty {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            SetState(change.Address, null);
                        }
                        else
                        {
                            if (_logger.IsTrace) _logger.Trace($"  Update {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            SetState(change.Address, change.Account);
                        }

                        break;
                    }
                    case ChangeType.New:
                    {
                        if (!releaseSpec.IsEip158Enabled || !change.Account.IsEmpty)
                        {
                            if (_logger.IsTrace) _logger.Trace($"  Create {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            SetState(change.Address, change.Account);
                        }

                        break;
                    }
                    case ChangeType.Delete:
                    {
                        if (_logger.IsTrace) _logger.Trace($"  Remove {change.Address}");
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
            //_state.UpdateRootHash(); // why here?
        }

        private Account GetState(Address address)
        {
            //Account cached = _longTermCache.Get(address);
            //if (cached != null)
            //{
            //    return cached;
            //}

            Metrics.StateTreeReads++;
            Account account = _state.Get(address);
            //_longTermCache.Set(address, account);
            return account;
        }

        private void SetState(Address address, Account account)
        {
            //_longTermCache.Set(address, account);
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
            if (_logger.IsTrace) _logger.Trace("Clearing state provider caches");
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