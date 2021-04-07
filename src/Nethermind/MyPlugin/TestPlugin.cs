using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Pipeline;
using Nethermind.Pipeline.Publishers;

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
            _pipeline = new Pipeline<Transaction>(pipelineSource);

            var checkIfContractCreationElement = new CheckIfContractCreationElement<Transaction>();
            _pipeline.AddElement(checkIfContractCreationElement);

            return Task.CompletedTask;
        }
        public Task InitNetworkProtocol()
        {
            var webSocketPublisher = new WebSocketsPublisher<Transaction>(_api.EthereumJsonSerializer);
            _api.WebSocketsManager.AddModule(webSocketPublisher);
            _api.Publishers.Add(webSocketPublisher);
            _pipeline.AddElement(webSocketPublisher);
            return Task.CompletedTask;
        }
        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}