using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.Dsl
{
    public class DslPlugin : INethermindPlugin
    {
        public string Name { get; }
        
        public string Description { get; }
        
        public string Author { get; }

        public Task Init(INethermindApi nethermindApi)
        {
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}