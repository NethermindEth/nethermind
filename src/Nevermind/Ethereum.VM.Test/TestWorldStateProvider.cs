using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Evm;
using Nevermind.Store;

namespace Ethereum.VM.Test
{
    public class TestWorldStateProvider : IWorldStateProvider
    {
        public TestWorldStateProvider(StateTree stateTree)
        {
            State = stateTree;
        }

        public StateTree State { get; }

        public Account GetOrCreateAccount(Address address)
        {
            Rlp rlp = State.Get(address);
            if (rlp.Bytes == null)
            {
                Account account = new Account();
                State.Set(address, Rlp.Encode(account));
                return GetOrCreateAccount(address);
            }

            return Rlp.Decode<Account>(rlp);
        }
    }
}