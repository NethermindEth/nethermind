using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.PubSub;
using Nethermind.Serialization.Json;
using Nethermind.State;
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
        private ILogger _logger;
        private ITxPool _txPool;
        private PipelineElement<Transaction> _pipelineElement;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _logger = nethermindApi.LogManager.GetClassLogger();

            if (_logger.IsInfo) _logger.Info("Pipeline plugin initialized");
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            // here, tx pool is not null
            _txPool = _api.TxPool;
            CreatePipelineElement();
            CreateBuilder();
            return Task.CompletedTask;
        }

        private void CreateBuilder()
        {
            var builder = new PipelineBuilder<Transaction, Transaction>(_pipelineElement);
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
