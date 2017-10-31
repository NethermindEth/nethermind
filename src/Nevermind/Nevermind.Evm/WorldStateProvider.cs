using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class WorldStateProvider : IWorldStateProvider
    {
        public WorldStateProvider(StateTree stateTree)
        {
            State = stateTree;
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
            State.Set(codeHash, new Rlp(code));
            return codeHash;
        }

        public byte[] GetCode(Keccak codeHash)
        {
            return State.Get(codeHash.Bytes);
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
            State.Set(address, Rlp.Encode(account));
        }
    }
}