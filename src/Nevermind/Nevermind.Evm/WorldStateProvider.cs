using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class WorldStateProvider : IWorldStateProvider
    {
        private const int StartCapacity = 1024;

        private readonly Dictionary<Address, Stack<int>> _cache = new Dictionary<Address, Stack<int>>();
        private readonly Dictionary<Keccak, byte[]> _code = new Dictionary<Keccak, byte[]>();

        private readonly HashSet<Address> _committedThisRound = new HashSet<Address>();

        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        public WorldStateProvider(StateTree stateTree)
        {
            State = stateTree;
        }

        public StateTree State { get; }

        public bool AccountExists(Address address)
        {
            if (_cache.ContainsKey(address))
            {
                return _changes[_cache[address].Peek()].ChangeType != ChangeType.Delete;
            }

            Account account = GetAndAddToCache(address);

            return account != null;
        }

        public bool IsEmptyAccount(Address address)
        {
            // TODO: assumed exists
            Account account = GetThroughCache(address);
            return account.Balance == BigInteger.Zero &&
                   account.Nonce == BigInteger.Zero &&
                   account.CodeHash == Keccak.OfAnEmptyString &&
                   account.StorageRoot == Keccak.EmptyTreeHash;
        }

        public BigInteger GetNonce(Address address)
        {
            Account account = GetThroughCache(address);
            return account?.Nonce ?? BigInteger.Zero;
        }

        public BigInteger GetBalance(Address address)
        {
            Account account = GetThroughCache(address);
            return account?.Balance ?? BigInteger.Zero;
        }

        public void UpdateCodeHash(Address address, Keccak codeHash)
        {
            if (ShouldLog.State)
            {
                Console.WriteLine($"  SETTING CODE HASH of {address} to {codeHash}");
            }

            Account account = GetThroughCache(address);
            if (account.CodeHash == codeHash)
            {
                return;
            }

            Account changedAccount = account.WithChangedCodeHash(codeHash);
            PushUpdate(address, changedAccount);
        }

        public void UpdateBalance(Address address, BigInteger balanceChange)
        {
            Account account = GetThroughCache(address);
            if (balanceChange == 0)
            {
                return;
            }

            Account changedAccount = account.WithChangedBalance(account.Balance + balanceChange);
            PushUpdate(address, changedAccount);
        }

        public void UpdateStorageRoot(Address address, Keccak storageRoot)
        {
            Account account = GetThroughCache(address);
            if (account.StorageRoot == storageRoot)
            {
                return;
            }

            Account changedAccount = account.WithChangedStorageRoot(storageRoot);
            PushUpdate(address, changedAccount);
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
            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            if (ShouldLog.State)
            {
                Console.WriteLine($"  RESTORING SNAPSHOT {snapshot}");
            }

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                _changes[_currentPosition - i] = null; // TODO: temp
                int forAssertion = _cache[change.Address].Pop();
                Debug.Assert(forAssertion == _currentPosition - i);

                if (_cache[change.Address].Count == 0)
                {
                    continue; // it will just be ignored in commit
                }

                int previousPosition = _cache[change.Address].Peek();
                Change previousValue = _changes[previousPosition];
                if (previousValue == null)

                {
                    switch (previousValue.ChangeType)
                    {
                        case ChangeType.JustCache:
                        case ChangeType.Update:
                        case ChangeType.New:
                            if (ShouldLog.State)
                            {
                                Console.WriteLine($"  UPDATE {previousValue.Address} B = {previousValue.Account.Balance} N = {previousValue.Account.Nonce}");
                            }
                            State.Set(previousValue.Address, Rlp.Encode(previousValue.Account));
                            break;
                        case ChangeType.Delete:
                            if (ShouldLog.State)
                            {
                                Console.WriteLine($"  DELETE {previousValue.Address}");
                            }
                            State.Set(previousValue.Address, null);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }

            _currentPosition = snapshot;
        }

        public void CreateAccount(Address address, BigInteger balance)
        {
            if (ShouldLog.State)
            {
                Console.WriteLine($"  CREATING ACCOUNT: {address} with balance {balance}");
            }

            Account account = new Account();
            account.Balance = balance;
            PushNew(address, account);
        }

        public void Commit()
        {
            if (ShouldLog.State)
            {
                Console.WriteLine("  COMMITTING CHANGES");
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
                        break;
                    case ChangeType.Update:
                        if (ShouldLog.State)
                        {
                            Console.WriteLine($"  UPDATE {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                        }
                        State.Set(change.Address, Rlp.Encode(change.Account));
                        break;
                    case ChangeType.New:
                        if (ShouldLog.State)
                        {
                            Console.WriteLine($"  CREATE {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                        }
                        State.Set(change.Address, Rlp.Encode(change.Account));
                        break;
                    case ChangeType.Delete:
                        if (ShouldLog.State)
                        {
                            Console.WriteLine($"  DELETE {change.Address}");
                        }

                        bool wasItCreatedNow = false;
                        while (_cache[change.Address].Count > 0)
                        {
                            int previousOne = _cache[change.Address].Pop();
                            wasItCreatedNow |= (_changes[previousOne].ChangeType == ChangeType.New);
                            if (wasItCreatedNow)
                            {
                                break;
                            }
                        }

                        if (!wasItCreatedNow)
                        {
                            State.Set(change.Address, null);
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            _currentPosition = -1;
            _committedThisRound.Clear();
            _cache.Clear();
            _changes = new Change[1024]; // TODO: remove after tests passing
        }

        private Account GetAccount(Address address)
        {
            Rlp rlp = State.Get(address);
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

        private void PushJustCache(Address address, Account changedAccount)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.JustCache, address, changedAccount);
        }

        private void PushUpdate(Address address, Account changedAccount)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.Update, address, changedAccount);
        }

        private void PushDelete(Address address)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.Delete, address, null);
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
    }
}