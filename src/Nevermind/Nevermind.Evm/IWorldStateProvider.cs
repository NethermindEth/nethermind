using Nevermind.Core;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public interface IWorldStateProvider
    {
        StateTree State { get; }

        Account GetOrCreateAccount(Address address);

        void UpdateAccount(Address address, Account account);

        Account GetAccount(Address address);
    }
}