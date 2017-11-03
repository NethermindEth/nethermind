using System.Collections.Generic;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class WorldStateProvider : IWorldStateProvider
    {
        private readonly bool _codeInState = false; // that is sure - with code outside of state genesis block hash works fine in one of the tests with an account with code (example add11)
        private readonly Dictionary<Keccak, byte[]> _code = new Dictionary<Keccak, byte[]>();

        public WorldStateProvider(StateTree stateTree)
        {
            State = stateTree;
        }

        public WorldStateProvider()
        {
        }

        public StateTree State { get; }

        public Account GetAccount(Address address)
        {
            Rlp rlp = State.Get(address);
            if (rlp.Bytes == null)
            {
                return null;
            }

            return Rlp.Decode<Account>(rlp);
        }

        public Keccak UpdateCode(byte[] code)
        {
            if (code.Length == 0)
            {
                return Keccak.OfAnEmptyString;
            }

            Keccak codeHash = Keccak.Compute(code);
            if (_codeInState)
            {
                State.Set(codeHash, new Rlp(code));    
            }
            else
            {
                _code[codeHash] = code;
            }
            
            return codeHash;
        }

        public byte[] GetCode(Keccak codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString)
            {
                return new byte[0];
            }

            if (_codeInState)
            {
                return State.Get(codeHash.Bytes);
            }
            else
            {
                return _code[codeHash];
            }
        }

        public StateSnapshot TakeSnapshot()
        {
            return State.TakeSnapshot();
        }

        public void Restore(StateSnapshot snapshot)
        {
            State.Restore(snapshot);
        }

        public Account GetOrCreateAccount(Address address)
        {
            Account account = GetAccount(address);
            if (account == null)
            {
                account = new Account();
                UpdateAccount(address, account);
            }

            return account;
        }

        public void UpdateAccount(Address address, Account account)
        {
            State.Set(address, account == null ? null : Rlp.Encode(account));
        }
    }
}