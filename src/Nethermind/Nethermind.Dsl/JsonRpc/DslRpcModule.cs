using System.Collections.Generic;
using System.Linq;
using Nethermind.Api;
using Nethermind.Dsl.ANTLR;
using Nethermind.JsonRpc;
using Nethermind.Logging;

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