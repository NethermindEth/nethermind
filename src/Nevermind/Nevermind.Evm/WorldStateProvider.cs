using System;
using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class WorldStateProvider : IWorldStateProvider
    {
        private readonly Dictionary<Keccak, byte[]> _code = new Dictionary<Keccak, byte[]>();

        private readonly Dictionary<Address, Account> _accountCache = new Dictionary<Address, Account>();

        public WorldStateProvider(StateTree stateTree)
        {
            State = stateTree;
        }

        public StateTree State { get; }

        private Account GetAccount(Address address)
        {
            Rlp rlp = State.Get(address);
            if (rlp.Bytes == null)
            {
                return null;
            }

            return Rlp.Decode<Account>(rlp);
        }

        public bool AccountExists(Address address)
        {
            if (_accountCache.ContainsKey(address))
            {
                return true;
            }

            Account account = GetAccount(address);
            if (account != null)
            {
                _accountCache[address] = account;
            }

            return account != null;
        }

        private Account GetThroughCache(Address address)
        {
            if (_accountCache.ContainsKey(address))
            {
                return _accountCache[address];
            }

            Account account = GetAccount(address);
            if (account != null)
            {
                _accountCache[address] = account;
            }
            
            return account;
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
            Account account = GetThroughCache(address);
            account.CodeHash = codeHash;
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  SETTING CODE HASH of {address} to {codeHash}");
            }

            UpdateAccount(address, account);
        }

        public void UpdateBalance(Address address, BigInteger balanceChange)
        {
            Account account = GetThroughCache(address);
            account.Balance += balanceChange;
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  SETTING BALANCE of  {address} to {account.Balance}");
            }

            UpdateAccount(address, account);
        }

        public void UpdateStorageRoot(Address address, Keccak storageRoot)
        {
            Account account = GetThroughCache(address);
            account.StorageRoot = storageRoot;
            UpdateAccount(address, account);
        }

        public void IncrementNonce(Address address)
        {
            if (ShouldLog.Evm)
            {
                //Console.WriteLine($"  SETTING NONCE of {address}");
            }

            Account account = GetThroughCache(address);
            account.Nonce++;
            if (ShouldLog.Evm)
            {
                //Console.WriteLine($"  SETTING NONCE of {address} to {account.Nonce}");
            }

            UpdateAccount(address, account);
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
            if (_accountCache.ContainsKey(address))
            {
                _accountCache.Remove(address);
            }

            UpdateAccount(address, null);
        }

        private class Change
        {
        }

        private List<Change> _changes = new List<Change>();

        public int TakeSnapshot()
        {
            return _changes.Count;
        }

        public void Restore(StateSnapshot snapshot)
        {
            _accountCache.Clear();
            State.Restore(snapshot);
        }

        public void CreateAccount(Address address, BigInteger balance)
        {
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  CREATING ACCOUNT: {address} with balance {balance}");
            }

            Account account = new Account();
            account.Balance = balance;

            _accountCache.Add(address, account);
            UpdateAccount(address, account);
        }

        private void UpdateAccount(Address address, Account account)
        {
            State.Set(address, account == null ? null : Rlp.Encode(account));
        }
    }
}