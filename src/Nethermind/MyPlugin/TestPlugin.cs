using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Pipeline;

namespace MyPlugin
{
    public class TestPlugin : INethermindPlugin
    {
        public string Name { get; } = "Test Plugin";
        public string Description { get; } = "";
        public string Author { get; } = "";
        private INethermindApi _api;
        private IPipeline<Transaction> _pipeline;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            PipelineSource<Transaction> pipelineSource = new PipelineSource<Transaction>(_api.TxPool); 
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