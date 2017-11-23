using System.Numerics;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class Account
    {
        public Account()
        {
            Balance = BigInteger.Zero;
            Nonce = BigInteger.Zero;
            CodeHash = Keccak.OfAnEmptyString;
            StorageRoot = Keccak.EmptyTreeHash;
        }

        public BigInteger Nonce { get; set; }
        public BigInteger Balance { get; set; }
        public Keccak StorageRoot { get; set; }
        public Keccak CodeHash { get; set; }

        public bool IsSimple => CodeHash == Keccak.OfAnEmptyString;

        public bool IsEmpty =>
            Balance == BigInteger.Zero &&
            Nonce == BigInteger.Zero &&
            CodeHash == Keccak.OfAnEmptyString;

        public Account WithChangedBalance(BigInteger newBalance)
        {
            Account account = new Account();
            account.Nonce = Nonce;
            account.Balance = newBalance;
            account.StorageRoot = StorageRoot;
            account.CodeHash = CodeHash;
            return account;
        }

        public Account WithChangedNonce(BigInteger newNonce)
        {
            Account account = new Account();
            account.Nonce = newNonce;
            account.Balance = Balance;
            account.StorageRoot = StorageRoot;
            account.CodeHash = CodeHash;
            return account;
        }

        public Account WithChangedStorageRoot(Keccak newStorageRoot)
        {
            Account account = new Account();
            account.Nonce = Nonce;
            account.Balance = Balance;
            account.StorageRoot = newStorageRoot;
            account.CodeHash = CodeHash;
            return account;
        }

        public Account WithChangedCodeHash(Keccak newCodeHash)
        {
            Account account = new Account();
            account.Nonce = Nonce;
            account.Balance = Balance;
            account.StorageRoot = StorageRoot;
            account.CodeHash = newCodeHash;
            return account;
        }
    }
}