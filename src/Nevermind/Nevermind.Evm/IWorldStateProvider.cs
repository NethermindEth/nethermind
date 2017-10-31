using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public interface IWorldStateProvider
    {
        Account GetOrCreateAccount(Address address);

        void UpdateAccount(Address address, Account account);

        Account GetAccount(Address address);

        Keccak UpdateCode(byte[] code);

        byte[] GetCode(Keccak codeHash);

        StateSnapshot TakeSnapshot();

        void Restore(StateSnapshot snapshot);
    }
}