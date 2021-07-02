using System.Collections.Generic;
using System.Linq;
using Nethermind.Api;
using Nethermind.Core.PubSub;
using Nethermind.Dsl.ANTLR;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using IPublisher = Nethermind.Pipeline.Publishers.IPublisher;

namespace Nethermind.Dsl.JsonRpc
{
    public class DslRpcModule : IDslRpcModule
    {
        private readonly INethermindApi _api;
        private readonly Dictionary<int, Interpreter> _interpreters;
        private readonly ILogger _logger;

        public DslRpcModule(INethermindApi api ,ILogger logger, Dictionary<int, Interpreter> interpreters)
        {
            _api = api;
            _logger = logger;            
            _interpreters = interpreters;
        }
        
        public ResultWrapper<int> dsl_addScript(string script)
        {
            if(_logger.IsInfo) _logger.Info($"Adding new DSL script. {script}");
            int scriptID = AddInterpreter(new Interpreter(_api, script));
            
            if(_logger.IsInfo) _logger.Info($"New DSL script added with ID: {scriptID}");
            return ResultWrapper<int>.Success(scriptID);
        }

        public ResultWrapper<bool> dsl_removeScript(int index)
        {
            bool result = _interpreters.Remove(index);

            return result == true ? ResultWrapper<bool>.Success(result)
                            : ResultWrapper<bool>.Fail("Adding new dsl script to the pool, failed.");
        }

        public ResultWrapper<string> dsl_inspectScript(int index)
        {
            var interpreter = _interpreters.GetValueOrDefault(index);

            return interpreter is null ? ResultWrapper<string>.Fail("Couldn't find pipeline with given ID") : ResultWrapper<string>.Success(interpreter.Script);
        }

        public ResultWrapper<bool> dsl_stopPublisher(int index)
        {
            var interpreter = _interpreters.GetValueOrDefault(index);
            if (interpreter is null) return ResultWrapper<bool>.Fail("Couldn't find pipeline with given ID");

            var blockPublisher = (IPublisher) interpreter.BlocksPipeline.Elements.Last();
            var transactionsPublisher = (IPublisher) interpreter.TransactionsPipeline.Elements.Last();
            var pendingTransactionsPublisher = (IPublisher) interpreter.PendingTransactionsPipeline.Elements.Last();
            var eventsPublisher = (IPublisher) interpreter.EventsPipeline.Elements.Last();

            blockPublisher.Stop();
            transactionsPublisher.Stop();
            pendingTransactionsPublisher.Stop();
            eventsPublisher.Stop();

            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<bool> dsl_startPublisher(int index)
        {
            var interpreter = _interpreters.GetValueOrDefault(index);
            if (interpreter is null) return ResultWrapper<bool>.Fail("Couldn't find pipeline with given ID");

            var blockPublisher = (IPublisher) interpreter.BlocksPipeline.Elements.Last();
            var transactionsPublisher = (IPublisher) interpreter.TransactionsPipeline.Elements.Last();
            var pendingTransactionsPublisher = (IPublisher) interpreter.PendingTransactionsPipeline.Elements.Last();
            var eventsPublisher = (IPublisher) interpreter.EventsPipeline.Elements.Last();

            blockPublisher.Start();
            transactionsPublisher.Start();
            pendingTransactionsPublisher.Start();
            eventsPublisher.Start();

            return ResultWrapper<bool>.Success(true);
        }

        private int AddInterpreter(Interpreter interpreter)
        {
            if(_interpreters?.Count == 0)
            {
                _interpreters.Add(1, interpreter);
                return 1;
            }

            int index = _interpreters.Last().Key + 1;
            _interpreters.Add(index, interpreter);
            return index;
        }
    }
}