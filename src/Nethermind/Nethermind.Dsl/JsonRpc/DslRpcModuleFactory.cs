using System.Collections.Generic;
using Nethermind.Api;
using Nethermind.Dsl.ANTLR;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.Dsl.JsonRpc
{
    public class DslRpcModuleFactory : ModuleFactoryBase<IDslRpcModule>
    {
        private readonly INethermindApi _api;
        private readonly ILogger _logger;
        private readonly Dictionary<int, Interpreter> _interpreters;
        public DslRpcModuleFactory(INethermindApi api, ILogger logger, Dictionary<int, Interpreter> interpreters)
        {
            _api = api;
            _logger = logger;
            _interpreters = interpreters;
        }

        public override IDslRpcModule Create()
        {
            return new DslRpcModule(_api, _logger, _interpreters);
        }
    }
}