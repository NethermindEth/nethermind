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

        public WorldStateProvider(StateTree stateTree)
        {
            State = stateTree;
        }

        public WorldStateProvider()
        {
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
            return GetAccount(address) != null;
        }

        public bool IsEmptyAccount(Address address)
        {
            Account account = GetAccount(address);
            return account.Balance == BigInteger.Zero &&
                   account.Nonce == BigInteger.Zero &&
                   account.CodeHash == Keccak.OfAnEmptyString &&
                   account.StorageRoot == Keccak.EmptyTreeHash;
        }

        public BigInteger GetNonce(Address address)
        {
            Account account = GetAccount(address);
            return account?.Nonce ?? BigInteger.Zero;
        }

        public BigInteger GetBalance(Address address)
        {
            Account account = GetAccount(address);
            return account?.Balance ?? BigInteger.Zero;
        }

        public void UpdateCodeHash(Address address, Keccak codeHash)
        {
            Account account = GetAccount(address);
            account.CodeHash = codeHash;
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  SETTING CODE HASH of {address} to {codeHash}");
            }

            UpdateAccount(address, account);
        }

        public void UpdateBalance(Address address, BigInteger balanceChange)
        {
            Account account = GetAccount(address);
            account.Balance += balanceChange;
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  SETTING BALANCE of  {address} to {account.Balance}");
            }

            UpdateAccount(address, account);
        }

        public void UpdateStorageRoot(Address address, Keccak storageRoot)
        {
            Account account = GetAccount(address);
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  SETTING STORAGE ROOT of {address} from {account.StorageRoot} to {storageRoot}");
            }

            account.StorageRoot = storageRoot;
            UpdateAccount(address, account);
        }

        public void IncrementNonce(Address address)
        {
            if (ShouldLog.Evm)
            {
                //Console.WriteLine($"  SETTING NONCE of {address}");
            }

            Account account = GetAccount(address);
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
            Account account = GetAccount(address);
            if (account == null)
            {
                return new byte[0];
            }

            return GetCode(account.CodeHash);
        }

        public void DeleteAccount(Address address)
        {
            UpdateAccount(address, null);
        }

        public StateSnapshot TakeSnapshot()
        {
            return State.TakeSnapshot();
        }

        public void Restore(StateSnapshot snapshot)
        {
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
            UpdateAccount(address, account);
        }

        private Account GetOrCreateAccount(Address address)
        {
            Account account = GetAccount(address);
            if (account == null)
            {
                account = new Account();
                UpdateAccount(address, account);
            }

            return account;
        }

        private void UpdateAccount(Address address, Account account)
        {
            State.Set(address, account == null ? null : Rlp.Encode(account));
        }
    }
}