using System.Collections.Generic;
using Nethermind.Api;
using Nethermind.Dsl.ANTLR;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.Dsl.JsonRpc
{
    public class DslRpcModule : IDslRpcModule
    {
        private readonly INethermindApi _api;
        private readonly List<Interpreter> _interpreters;
        private readonly ILogger _logger;

        public DslRpcModule(INethermindApi api ,ILogger logger, List<Interpreter> interpreters)
        {
            _api = api;
            _logger = logger;            
            _interpreters = interpreters;
        }
        public ResultWrapper<int> dsl_addScript(string script)
        {
            _interpreters.Add(new Interpreter(_api, script));
            return 1;
        }
    }
}