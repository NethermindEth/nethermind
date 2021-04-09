using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Pipeline.Publishers;
using Nethermind.Serialization.Json;
using Nethermind.TxPool;

namespace Nethermind.Pipeline.TestPlugin
{
    //plugin do streamowania ilosci tx z txpoola
    public class TestPlugin : INethermindPlugin
    {
        public string Name { get; } = "Pipeline Plugin";
        public string Description { get; } = "Pipeline plugin streaming data";
        public string Author { get; } = "Nethermind Team";
        private INethermindApi _api;
        public IJsonSerializer _jsonSerializer;
        private ILogger _logger;
        private ITxPool _txPool;
        private PipelineElement<Transaction> _pipelineElement;
        private WebSocketsPublisher<Transaction, Transaction> _wsPublisher;
        private PipelineBuilder<Transaction, Transaction> _builder;
        private IPipeline _pipeline;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _logger = _api.LogManager.GetClassLogger();
            _jsonSerializer = _api.EthereumJsonSerializer;
            
            if (_logger.IsInfo) _logger.Info("Pipeline plugin initialized");
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            // here, tx pool is not null
            _txPool = _api.TxPool;
            CreatePipelineElement();
            CreateWsPipelineElement();
            CreateBuilder();
            BuildPipeline();
            
            return Task.CompletedTask;
        }

        private void BuildPipeline()
        {
            _pipeline = _builder.Build();
        }

        private void CreateWsPipelineElement()
        {
            _wsPublisher = new WebSocketsPublisher<Transaction, Transaction>(_jsonSerializer);
            _api.WebSocketsManager.AddModule(_wsPublisher);
        }

        private void CreateBuilder()
        {
            _builder = new PipelineBuilder<Transaction, Transaction>(_pipelineElement);
            _builder.AddElement(_wsPublisher);
        }

        private void CreatePipelineElement()
        {
            _pipelineElement = new PipelineElement<Transaction>(_txPool);
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}
