using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Dsl.ANTLR;
using System.IO;
using Nethermind.Logging;
using System.IO.Abstractions;
using System.Collections.Generic;
using Nethermind.Dsl.Pipeline;
using Nethermind.Dsl.JsonRpc;
using System.Linq;
using Nethermind.JsonRpc.Modules;
using System;

#nullable enable
namespace Nethermind.Dsl
{
    public class DslPlugin : INethermindPlugin
    {
        public string Name { get; } = "DslPlugin";
        public string Description { get; } = "Plugin created in order to let users create their own DSL scripts used in data extraction from chain";
        public string Author { get; } = "Nethermind team";
        public IFileSystem? FileSystem;
        private INethermindApi? _api;
        private ParseTreeListener? _listener;
        private Dictionary<int, Interpreter>? _interpreters;
        private IDslRpcModule? _rpcModule;
        private ILogger? _logger;

        public async Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;

            _logger = _api.LogManager.GetClassLogger();
            if (_logger.IsInfo) _logger.Info("Initializing DSL plugin ...");

            IEnumerable<string> dslScripts = LoadDSLScript();
            _interpreters = new Dictionary<int, Interpreter>();

            if (dslScripts != null && dslScripts.Count() != 0)
            {
                foreach (var script in dslScripts)
                {
                    AddInterpreter(new Interpreter(_api, script));
                }
            }

            if (_logger.IsInfo) _logger.Info($"DSL plugin initialized with {_interpreters.Count} scripts loaded at the start of the node.");
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if(_logger.IsInfo) _logger.Info("Initializing DSL RPC module...");
            var rpcPool = new SingletonModulePool<IDslRpcModule>(new DslRpcModuleFactory(_api, _logger, _interpreters));

            _api.RpcModuleProvider.Register(rpcPool);

            if(_logger.IsInfo) _logger.Info("Initialized DSL RPC module correctly.");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

        private IEnumerable<string> LoadDSLScript()
        {
            if (FileSystem == null)
            {
                FileSystem = new FileSystem();
            }

            var dirPath = FileSystem.Path.Combine(PathUtils.ExecutingDirectory, "DSL");
            if (_logger.IsInfo) _logger.Info($"Loading dsl scripts from {dirPath}");

            if (FileSystem.Directory.Exists(dirPath))
            {
                string[] files = FileSystem.Directory.GetFiles("DSL", "*.txt");

                if(files.Length == 0)
                {
                    if(_logger.IsInfo) _logger.Info($"No DSL scripts were found at the start of the plugin in the {dirPath}");
                    yield break;
                }

                foreach(var file in files)
                {
                    yield return FileSystem.File.ReadAllText(file);
                }
            }
            else
            {
                throw new FileLoadException($"Could not find DSL directory at {dirPath} or the directory is empty");
            }
        }
    }
}
