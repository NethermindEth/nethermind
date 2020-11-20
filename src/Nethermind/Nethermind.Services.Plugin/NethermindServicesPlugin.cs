using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.Services.Plugin
{
    public class NethermindServicesPlugin: INethermindPlugin
    {       
        private INethermindApi _api;

        public void Dispose()
        {
        }

        public string Name => "NethermindServices";

        public string Description => "Various Nethermind Services that can be added to Web Host";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _api = api;
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}
