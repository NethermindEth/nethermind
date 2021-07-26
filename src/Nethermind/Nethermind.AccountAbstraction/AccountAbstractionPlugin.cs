using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Network;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionPlugin : INethermindPlugin
    {
        public string Name => "Account Abstraction";

        public string Description => "Implements account abstraction via alternative mempool";

        public string Author => "Nethermind";

        public Task Init(INethermindApi nethermindApi)
        {
            throw new System.NotImplementedException();
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
