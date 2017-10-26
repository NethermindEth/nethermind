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

        public Account GetOrCreateAccount(Address address)
        {
            Rlp rlp = State.Get(address);
            return rlp.Bytes == null ? null : Rlp.Decode<Account>(rlp);
        }
    }
}